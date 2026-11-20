using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lis.Connections;
using Lis.Connections.RawMessages;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

/// <summary>Agent gửi bản tin thô: hàng chờ đĩa, server giả bằng <see cref="HttpMessageHandler"/>.</summary>
public sealed class RawMessageUploaderTests : IDisposable
{
    private const string Oru =
        "MSH|^~\\&|Analyzer|Mindray|||20261108090000||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        "PID|1||BN0001^^^^MR||Mẫu^Thử\r" +
        "OBR|1||2611080001|00001^Automated Count^99MRC\r" +
        "OBX|1|NM|6690-2^WBC^LN||9.81|10*9/L|4.00-10.00|N|||F\r";

    private static readonly Guid Machine = Guid.NewGuid();
    private static readonly Guid OtherMachine = Guid.NewGuid();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-rawup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(7)));
    private readonly RawMessageQueue _queue;
    private readonly FakeServer _server = new();

    public RawMessageUploaderTests() => _queue = new RawMessageQueue(Path.Combine(_root, "cho-gui", "ban-tin"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private readonly ListLogger _log = new();

    private RawMessageUploader Uploader(RawMessageQueue? queue = null) =>
        new(queue ?? _queue, new ServerClient(new HttpClient(_server) { BaseAddress = new Uri("http://may-chu.thu/") }), _clock, _log);

    private async Task<RawMessage> AddBigAsync(long seq, int length)
    {
        var message = new RawMessage(
            Guid.CreateVersion7(), Machine, seq, "phien-1", _clock.GetUtcNow(), _clock.GetUtcNow(), new byte[length], false);
        await _queue.SaveAsync(message, TestContext.Current.CancellationToken);
        return message;
    }

    private async Task<RawMessage> AddAsync(long seq, Guid? machine = null)
    {
        var message = new RawMessage(
            Guid.CreateVersion7(), machine ?? Machine, seq, "phien-1", _clock.GetUtcNow(), _clock.GetUtcNow(), Encoding.ASCII.GetBytes($"MSH|Thử|{seq}"), false);
        await _queue.SaveAsync(message, TestContext.Current.CancellationToken);
        return message;
    }

    [Fact]
    public async Task Mat_mang_giu_tep_co_mang_gui_du_theo_thu_tu()
    {
        var sent = new List<RawMessage>();
        for (var seq = 1; seq <= 120; seq++)
        {
            sent.Add(await AddAsync(seq));
        }

        var uploader = Uploader();
        _server.Handler = _ => throw new HttpRequestException("mất mạng");

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(120);
        _queue.Count().ShouldBe(120);

        // Đang chờ lùi nhịp: tick thường chưa gửi lại; quá 1,2 giây thì gửi.
        _server.Requests.Clear();
        _server.Handler = FakeServer.SavedAll;
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(120);
        _server.Requests.ShouldBeEmpty();
        _clock.Advance(TimeSpan.FromSeconds(1.3));
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // 120 bản tin = 3 lô (50 + 50 + 20), đúng thứ tự Seq.
        _server.Requests.Select(r => r.Count).ShouldBe([50, 50, 20]);
        _server.Requests.SelectMany(r => r).ShouldBe(sent.Select(m => m.Id));
    }

    [Fact]
    public async Task Lo_sau_chi_gui_khi_lo_truoc_xong_dut_mang_giua_chung_giu_phan_con_lai()
    {
        for (var seq = 1; seq <= 60; seq++)
        {
            await AddAsync(seq);
        }

        var calls = 0;
        _server.Handler = request => ++calls == 1 ? FakeServer.SavedAll(request) : throw new HttpRequestException("đứt mạng");

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(10);
        _queue.Peek(Machine, 100).Select(m => m.Seq).ShouldBe(Enumerable.Range(51, 10).Select(i => (long)i));
    }

    [Fact]
    public async Task Server_tra_trung_xoa_tep()
    {
        await AddAsync(1);
        _server.Handler = request => FakeServer.Reply(request, _ => "trung");

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);
        _queue.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Khong_thuoc_agent_giu_tep_thu_lai_sau_5_phut()
    {
        var foreign = await AddAsync(1, OtherMachine);
        await AddAsync(1);
        _server.Handler = request => FakeServer.Reply(request, m => m.InstrumentId == OtherMachine ? "khong-thuoc-agent" : "da-luu");
        var uploader = Uploader();

        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        _queue.Count(OtherMachine).ShouldBe(1); // không xoá
        _queue.Count(Machine).ShouldBe(0); // máy khác vẫn gửi bình thường
        _server.Requests.Count.ShouldBe(2);

        _clock.Advance(TimeSpan.FromMinutes(4));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Requests.Count.ShouldBe(2); // còn đang tạm bỏ qua

        _clock.Advance(TimeSpan.FromMinutes(1.1));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Requests.Count.ShouldBe(3);
        _server.Requests[2].ShouldBe([foreign.Id]);
        _queue.Count(OtherMachine).ShouldBe(1);
    }

    [Fact]
    public async Task Khong_hop_le_doi_ten_loi()
    {
        var bad = await AddAsync(1);
        await AddAsync(2);
        _server.Handler = request => FakeServer.Reply(request, m => m.Id == bad.Id ? "khong-hop-le" : "da-luu");

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);

        var files = Directory.GetFiles(Path.Combine(_root, "cho-gui", "ban-tin", Machine.ToString("N")));
        files.Single().ShouldEndWith($"{bad.Seq:D20}-{bad.Id:N}.json.loi");
        _queue.MaxSeq(Machine).ShouldBe(1);
    }

    [Fact]
    public async Task Bi_thu_hoi_dung_gui()
    {
        await AddAsync(1);
        await AddAsync(2, OtherMachine);
        _server.Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var uploader = Uploader();

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(2);
        _server.Calls.ShouldBe(1); // dừng ngay, không thử máy kế tiếp

        _clock.Advance(TimeSpan.FromHours(1));
        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(2);
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        _server.Calls.ShouldBe(1);
        _queue.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Server_loi_5xx_giu_tep_va_lui_nhip()
    {
        await AddAsync(1);
        _server.Handler = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var uploader = Uploader();

        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        _server.Calls.ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Calls.ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Calls.ShouldBe(2);
        _queue.Count().ShouldBe(1);
    }

    [Fact]
    public void Lui_nhip_1_2_4_den_toi_da_60_giay_cong_tru_20_phan_tram()
    {
        RawMessageUploader.BackoffFor(1, 0.5).ShouldBe(TimeSpan.FromSeconds(1));
        RawMessageUploader.BackoffFor(2, 0.5).ShouldBe(TimeSpan.FromSeconds(2));
        RawMessageUploader.BackoffFor(3, 0.5).ShouldBe(TimeSpan.FromSeconds(4));
        RawMessageUploader.BackoffFor(20, 0.5).ShouldBe(TimeSpan.FromSeconds(60));
        RawMessageUploader.BackoffFor(3, 0).ShouldBe(TimeSpan.FromSeconds(3.2));
        RawMessageUploader.BackoffFor(3, 1).ShouldBe(TimeSpan.FromSeconds(4.8));
    }

    [Fact]
    public async Task Flush_pending_gui_ngay_tra_so_con_lai()
    {
        await AddAsync(1);
        await AddAsync(2);
        await AddAsync(3, OtherMachine);
        _server.Handler = request => FakeServer.Reply(request, m => m.InstrumentId == OtherMachine ? "khong-thuoc-agent" : "da-luu");
        var uploader = Uploader();

        var result = await new FlushPendingHandler(uploader).RunAsync(null, TestContext.Current.CancellationToken);

        result!.Value.GetProperty("remaining").GetInt32().ShouldBe(1);
        _queue.Count(Machine).ShouldBe(0);

        // Flush không chờ nhịp tạm bỏ qua 5 phút: bấm lại là thử lại ngay.
        await new FlushPendingHandler(uploader).RunAsync(null, TestContext.Current.CancellationToken);
        _server.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Khoa_agent_khong_nam_trong_url_hay_than_ban_tin()
    {
        await AddAsync(1);
        var http = new HttpClient(_server) { BaseAddress = new Uri("http://may-chu.thu/") };
        http.DefaultRequestHeaders.Add(ServerClient.KeyHeader, "KHOA-BI-MAT-CUA-AGENT");
        await new RawMessageUploader(_queue, new ServerClient(http), _clock, NullLogger.Instance).FlushNowAsync(TestContext.Current.CancellationToken);

        _server.Urls.ShouldAllBe(u => !u.Contains("KHOA-BI-MAT") && u.EndsWith("agent/v1/raw-messages"));
        _server.Bodies.ShouldAllBe(b => !b.Contains("KHOA-BI-MAT"));
    }

    private InstrumentConnectionManager Manager(Link link)
    {
        var instrument = new InstrumentInfo(
            Machine, "Mindray BC-5380", "BC-5380", "hl7-mllp", null,
            new ConnectionSettings { Kind = ConnectionKind.TcpServer, TcpPort = 5100 }.ToJson(), "MT01", true);
        return new InstrumentConnectionManager(
            () => Task.FromResult<IReadOnlyList<InstrumentInfo>>([instrument]), "MT01", new NoLog(), new RawModeReadingSink(), new NoQueries(),
            Path.Combine(_root, "cho-gui"), TimeProvider.System, _ => link, _queue);
    }

    private async Task WaitForQueueAsync(int count)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (_queue.Count(Machine) < count)
        {
            (DateTime.UtcNow < end).ShouldBeTrue("hết giờ chờ bản tin xuống đĩa");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Manager_agent_dung_che_do_ban_tin_tho()
    {
        var link = new Link();
        var manager = Manager(link);
        await manager.ReloadAsync();
        link.Incoming.Writer.TryWrite([0x0B, .. Encoding.UTF8.GetBytes(Oru), 0x1C, 0x0D]);
        await WaitForQueueAsync(1);

        // RawModeReadingSink ném lỗi nếu bị gọi; bản tin nằm ở hàng chờ mới, không có tệp kết quả giải mã kiểu cũ.
        Directory.Exists(Path.Combine(_root, "cho-gui", Machine.ToString("N"))).ShouldBeFalse();
        var status = manager.StatusOf(Machine);
        status.SamplesReceived.ShouldBe(1);
        status.LastResultAt.ShouldNotBeNull();
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task Manager_dua_MaxSeq_that_tu_hang_cho_vao_runner()
    {
        const long seed = 4_000_000_000_000_000L; // lớn hơn mọi Seq theo đồng hồ
        await AddAsync(seed);
        var link = new Link();
        var manager = Manager(link);
        await manager.ReloadAsync();
        link.Incoming.Writer.TryWrite([0x0B, .. Encoding.UTF8.GetBytes(Oru), 0x1C, 0x0D]);
        await WaitForQueueAsync(2);

        // Seq mới do runner cấp phải lớn hơn Seq lớn nhất đã có trên đĩa (MaxSeq thật đi từ hàng chờ vào bộ gom).
        var seqs = _queue.Peek(Machine, 10).Select(m => m.Seq).ToList();
        seqs.Count.ShouldBe(2);
        seqs[0].ShouldBe(seed);
        seqs[1].ShouldBeGreaterThan(seed);
        await manager.StopAllAsync();
    }

    [Fact]
    public void Tep_ket_qua_dang_cu_chi_canh_bao_khong_bi_dung_toi()
    {
        var paths = new AgentPaths(_root);
        AgentWorker.HasLegacyPending(paths).ShouldBeFalse();

        Directory.CreateDirectory(paths.PendingMessages); // hàng chờ mới không tính là dạng cũ
        File.WriteAllText(Path.Combine(paths.PendingMessages, "x.json"), "{}");
        AgentWorker.HasLegacyPending(paths).ShouldBeFalse();

        var old = Path.Combine(paths.Pending, Machine.ToString("N"));
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "kq.json"), "{}");
        AgentWorker.HasLegacyPending(paths).ShouldBeTrue();
        File.Exists(Path.Combine(old, "kq.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ban_tin_qua_4MB_khong_gui_doi_ten_loi_ban_sau_van_di()
    {
        var big = await AddBigAsync(1, RawMessageCollector.MaxMessageBytes + 1);
        var ok = await AddAsync(2);

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);

        _server.Requests.ShouldBe([[ok.Id]]);
        _server.Bodies.ShouldAllBe(b => !b.Contains(big.Id.ToString()));
        Directory.GetFiles(Path.Combine(_root, "cho-gui", "ban-tin", Machine.ToString("N"))).Single()
            .ShouldEndWith($"{big.Seq:D20}-{big.Id:N}.json.loi");
        _log.Errors.ShouldContain(e => e.Contains($"Bản tin quá lớn ({RawMessageCollector.MaxMessageBytes + 1} byte) — không gửi được, đã chuyển .loi"));
    }

    [Fact]
    public async Task Ban_tin_dung_4MB_van_duoc_gui()
    {
        await AddBigAsync(1, RawMessageCollector.MaxMessageBytes);

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);
        _server.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Loi_4xx_chi_lui_nhip_may_do_may_khac_van_gui(HttpStatusCode code)
    {
        var bad = await AddAsync(1, OtherMachine);
        await AddAsync(1);
        _server.Handler = request =>
            FakeServer.Parse(request).All(m => m.InstrumentId == OtherMachine) ? new HttpResponseMessage(code) : FakeServer.SavedAll(request);
        var uploader = Uploader();

        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        _server.Calls.ShouldBe(2); // máy lỗi bị thử một lần, máy kia vẫn gửi xong trong cùng lượt
        _queue.Count(Machine).ShouldBe(0);
        _queue.Count(OtherMachine).ShouldBe(1);

        // Đang lùi nhịp riêng: máy lỗi chưa được thử lại, máy kia có bản tin mới vẫn gửi ngay.
        var next = await AddAsync(2);
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Calls.ShouldBe(3);
        _server.Requests[2].ShouldBe([next.Id]);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Requests[^1].ShouldBe([bad.Id]);
    }

    [Fact]
    public async Task Mat_ket_noi_hoac_5xx_van_lui_nhip_ca_luot_khong_thu_may_khac()
    {
        await AddAsync(1);
        await AddAsync(1, OtherMachine);
        _server.Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        (await Uploader().TickAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        _server.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Server_tra_thieu_id_ban_tin_thieu_va_cac_ban_sau_no_o_lai_gui_lai_nhip_sau()
    {
        var m1 = await AddAsync(1);
        var m2 = await AddAsync(2);
        var m3 = await AddAsync(3);
        var calls = 0;
        _server.Handler = request =>
            ++calls == 1 ? FakeServer.Reply(request, m => m.Id == m2.Id ? null : "da-luu") : FakeServer.SavedAll(request);

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);

        // Bản tin Seq 2 không được xác nhận nên Seq 3 cũng giữ lại, để Seq 2 luôn đi trước.
        _server.Requests.ShouldBe([[m1.Id, m2.Id, m3.Id], [m2.Id, m3.Id]]);
    }

    [Fact]
    public async Task Server_khong_xac_nhan_ban_tin_nao_lui_nhip_may_do()
    {
        await AddAsync(1);
        _server.Handler = request => FakeServer.Reply(request, _ => null);
        var uploader = Uploader();

        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        _server.Calls.ShouldBe(1);
        await uploader.TickAsync(TestContext.Current.CancellationToken);
        _server.Calls.ShouldBe(1); // đang lùi nhịp
    }

    [Fact]
    public async Task Server_tra_ket_qua_dao_thu_tu_van_khop_theo_id()
    {
        for (var seq = 1; seq <= 3; seq++)
        {
            await AddAsync(seq);
        }

        _server.Handler = request =>
        {
            var messages = FakeServer.Parse(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new { results = messages.AsEnumerable().Reverse().Select(m => new { id = m.Id, status = "da-luu", decodeStatus = "da-giai-ma" }) },
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
        };

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);
        _server.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Server_tra_id_la_trung_bi_bo_qua_khong_xoa_nham_ban_tin_nao()
    {
        var m1 = await AddAsync(1);
        var stranger = Guid.CreateVersion7();
        var calls = 0;
        _server.Handler = request =>
        {
            if (++calls > 1)
            {
                return FakeServer.SavedAll(request);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new { results = new[] { new { id = stranger, status = "trung", decodeStatus = "da-giai-ma" } } },
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
        };
        var uploader = Uploader();

        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(1); // id lạ không xoá được gì
        _queue.Peek(Machine, 5).Single().Id.ShouldBe(m1.Id);

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Xoa_tep_loi_IO_o_ban_tin_k_khong_gui_tiep_cac_ban_sau_cho_may_do_may_khac_van_gui()
    {
        var m1 = await AddAsync(1);
        var m2 = await AddAsync(2);
        var m3 = await AddAsync(3);
        await AddAsync(1, OtherMachine);
        var queue = new FailingDeleteQueue(Path.Combine(_root, "cho-gui", "ban-tin")) { FailOn = m2.Id };
        var uploader = Uploader(queue);

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(2);

        queue.Peek(Machine, 10).Select(m => m.Id).ShouldBe([m2.Id, m3.Id]); // m1 đã xoá; m2 kẹt; m3 giữ để không chen lên trước m2
        queue.Count(OtherMachine).ShouldBe(0);
        _server.Requests.Count(r => r.Contains(m1.Id)).ShouldBe(1);

        // Hết lỗi: gửi lại m2, m3 (server báo trùng) rồi xoá.
        queue.FailOn = null;
        _server.Handler = request => FakeServer.Reply(request, _ => "trung");
        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);
    }

    [Fact]
    public async Task Tep_dau_hang_bi_khoa_khong_gui_ban_tin_sau_mo_khoa_gui_du_theo_thu_tu()
    {
        var m1 = await AddAsync(1);
        var m2 = await AddAsync(2);
        var path = Path.Combine(_root, "cho-gui", "ban-tin", Machine.ToString("N"), $"{1:D20}-{m1.Id:N}.json");
        var uploader = Uploader();

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(2);
            _server.Calls.ShouldBe(0); // Seq 2 không được gửi trước Seq 1
        }

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);
        _server.Requests.ShouldBe([[m1.Id, m2.Id]]);
    }

    private static HttpResponseMessage TimedOut() => throw new TaskCanceledException("hết giờ", new TimeoutException());

    private async Task<List<RawMessage>> AddManyAsync(int count)
    {
        var all = new List<RawMessage>();
        for (var seq = 1; seq <= count; seq++)
        {
            all.Add(await AddAsync(seq));
        }

        return all;
    }

    [Fact]
    public async Task Het_gio_HTTP_giam_nua_co_lo_roi_thanh_cong_lien_tiep_3_lo_thi_tang_lai()
    {
        await AddManyAsync(120);
        var uploader = Uploader();
        var calls = 0;
        _server.Handler = request => ++calls == 1 ? TimedOut() : FakeServer.SavedAll(request);

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(120); // hết giờ: giữ nguyên
        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).Remaining.ShouldBe(0);

        // 50 (hết giờ) → 25, 25, 25 (3 lô liên tiếp) → nới lại 50: còn 120 - 75 = 45
        _server.Requests.Select(r => r.Count).ShouldBe([50, 25, 25, 25, 45]);
    }

    [Fact]
    public async Task Het_gio_lien_tiep_giam_dan_toi_thieu_1_ban_tin()
    {
        await AddManyAsync(60);
        var uploader = Uploader();
        _server.Handler = _ => TimedOut();

        for (var i = 0; i < 8; i++)
        {
            await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        }

        _server.Requests.Select(r => r.Count).ShouldBe([50, 25, 12, 6, 3, 1, 1, 1]);
        _queue.Count().ShouldBe(60);
    }

    [Fact]
    public async Task Co_lo_theo_tung_may_het_gio_o_may_nay_khong_lam_nho_lo_may_kia()
    {
        for (var seq = 1; seq <= 60; seq++)
        {
            await AddAsync(seq);
            await AddAsync(seq, OtherMachine);
        }

        var uploader = Uploader();
        var first = true;
        _server.Handler = request =>
        {
            if (first)
            {
                first = false;
                return TimedOut();
            }

            return FakeServer.SavedAll(request);
        };

        await uploader.FlushNowAsync(TestContext.Current.CancellationToken); // máy đầu hết giờ → dừng lượt
        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);

        var sizes = _server.Requests.Select(r => r.Count).ToList();
        sizes[0].ShouldBe(50);
        sizes[1].ShouldBe(25); // máy đã hết giờ: nửa lô
        sizes.Skip(1).Count(c => c == 50).ShouldBeGreaterThan(0); // máy kia vẫn lô đầy
        _queue.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Het_gio_do_nguoi_huy_khong_giam_co_lo()
    {
        await AddManyAsync(3);
        var uploader = Uploader();
        using var cts = new CancellationTokenSource();
        _server.Handler = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => uploader.FlushNowAsync(cts.Token));
        _server.Handler = FakeServer.SavedAll;
        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        _server.Requests[^1].Count.ShouldBe(3);
    }

    [Fact]
    public async Task Server_tra_mot_phan_4_id_dau_thi_xoa_4_giu_6_luot_sau_gui_lai_tu_ban_thu_5()
    {
        var sent = await AddManyAsync(10);
        var uploader = Uploader();
        var first = new HashSet<Guid>(sent.Take(4).Select(m => m.Id));
        var calls = 0;
        _server.Handler = request => ++calls == 1 ? FakeServer.Reply(request, m => first.Contains(m.Id) ? "da-luu" : null) : FakeServer.SavedAll(request);

        await uploader.TickAsync(TestContext.Current.CancellationToken);

        _server.Requests[0].Count.ShouldBe(10);
        _server.Requests[1].ShouldBe(sent.Skip(4).Select(m => m.Id).ToList());
        _queue.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Server_tra_mot_phan_roi_dung_lai_6_tep_o_lai_tren_dia()
    {
        var sent = await AddManyAsync(10);
        var uploader = Uploader();
        var first = new HashSet<Guid>(sent.Take(4).Select(m => m.Id));
        _server.Handler = request => FakeServer.Reply(request, m => first.Contains(m.Id) ? "da-luu" : null);

        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);

        _queue.Count().ShouldBe(6);
        _queue.Peek(Machine, 50).Select(m => m.Id).ShouldBe(sent.Skip(4).Select(m => m.Id).ToList());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Loi_408_429_lui_nhip_ca_luot_khong_phai_rieng_may_do(HttpStatusCode code)
    {
        await AddAsync(1);
        await AddAsync(1, OtherMachine);
        var uploader = Uploader();
        _server.Handler = _ => new HttpResponseMessage(code);

        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);

        _server.Calls.ShouldBe(1); // dừng ngay, không thử máy kế tiếp
        _server.Handler = FakeServer.SavedAll;
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(2); // còn đang lùi nhịp cả lượt
        _server.Calls.ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(2));
        (await uploader.TickAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Peek_rong_ma_hang_con_ban_tin_thi_canh_bao_kem_ten_tep_toi_da_1_lan_moi_phut()
    {
        var m1 = await AddAsync(1);
        var path = Path.Combine(_root, "cho-gui", "ban-tin", Machine.ToString("N"), $"{1:D20}-{m1.Id:N}.json");
        var uploader = Uploader();

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
            await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
            var warnings = _log.Warnings.Where(w => w.Contains(Path.GetFileName(path))).ToList();
            warnings.Count.ShouldBe(1);
            warnings[0].ShouldNotContain("X-Agent-Key");

            _clock.Advance(TimeSpan.FromSeconds(61));
            await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
            _log.Warnings.Count(w => w.Contains(Path.GetFileName(path))).ShouldBe(2);
        }
    }

    [Fact]
    public async Task Khong_thuoc_agent_log_canh_bao_moi_lan_thu_lai()
    {
        await AddAsync(1, OtherMachine);
        var uploader = Uploader();
        _server.Handler = request => FakeServer.Reply(request, _ => "khong-thuoc-agent");

        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);
        await uploader.FlushNowAsync(TestContext.Current.CancellationToken);

        _log.Warnings.Count(w => w.Contains("không thuộc agent này")).ShouldBe(2);
        _queue.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Flush_chay_song_song_voi_Tick_khong_gui_trung()
    {
        var all = new List<RawMessage>();
        for (var seq = 1; seq <= 3; seq++)
        {
            all.Add(await AddAsync(seq));
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _server.Handler = request =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return FakeServer.SavedAll(request);
        };
        var uploader = Uploader();

        var tick = Task.Run(() => uploader.TickAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        var flush = uploader.FlushNowAsync(TestContext.Current.CancellationToken); // chờ cổng, chưa gửi
        flush.IsCompleted.ShouldBeFalse();
        release.Set();

        (await tick).ShouldBe(0);
        (await flush).ShouldBe(new FlushResult(0, false));
        _server.Requests.SelectMany(r => r).ShouldBe(all.Select(m => m.Id)); // mỗi bản tin đúng một lần
    }

    [Fact]
    public async Task Flush_cong_dang_ban_qua_5_giay_tra_ngay_con_lai_kem_busy()
    {
        await AddAsync(1);
        await AddAsync(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _server.Handler = request =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return FakeServer.SavedAll(request);
        };
        var uploader = Uploader();
        var tick = Task.Run(() => uploader.TickAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();

        var handler = new FlushPendingHandler(uploader);
        var run = handler.RunAsync(null, TestContext.Current.CancellationToken);
        run.IsCompleted.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(4.9));
        run.IsCompleted.ShouldBeFalse(); // chưa đủ 5 giây: còn chờ
        _clock.Advance(TimeSpan.FromSeconds(0.2));

        var result = (await run)!.Value;
        result.GetProperty("remaining").GetInt32().ShouldBe(2);
        result.GetProperty("busy").GetBoolean().ShouldBeTrue();

        release.Set();
        await tick;

        // Rảnh thì không có busy.
        var idle = (await handler.RunAsync(null, TestContext.Current.CancellationToken))!.Value;
        idle.GetProperty("remaining").GetInt32().ShouldBe(0);
        idle.TryGetProperty("busy", out _).ShouldBeFalse();
    }

    private sealed class FailingDeleteQueue(string root) : RawMessageQueue(root)
    {
        public Guid? FailOn { get; set; }

        public override void Delete(RawMessage message)
        {
            if (message.Id == FailOn)
            {
                throw new IOException("tệp đang bị diệt vi-rút giữ");
            }

            base.Delete(message);
        }
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Errors { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
            else if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = SavedAll;

        /// <summary>Mỗi phần tử = các Id trong một lần gọi, theo thứ tự gửi.</summary>
        public List<List<Guid>> Requests { get; } = [];

        public List<string> Urls { get; } = [];

        public List<string> Bodies { get; } = [];

        public int Calls { get; private set; }

        public static HttpResponseMessage SavedAll(HttpRequestMessage request) => Reply(request, _ => "da-luu");

        /// <summary>Trả về <c>null</c> cho một bản tin = server bỏ sót, không nhắc tới Id đó.</summary>
        public static HttpResponseMessage Reply(HttpRequestMessage request, Func<(Guid Id, Guid InstrumentId), string?> status)
        {
            var messages = Parse(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        results = messages.Select(m => (m.Id, Status: status(m)))
                            .Where(r => r.Status is not null)
                            .Select(r => new { id = r.Id, status = r.Status, decodeStatus = "da-giai-ma" }),
                    },
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
        }

        public static List<(Guid Id, Guid InstrumentId)> Parse(HttpRequestMessage request)
        {
            using var doc = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return doc.RootElement.GetProperty("messages").EnumerateArray()
                .Select(e => (e.GetProperty("id").GetGuid(), e.GetProperty("instrumentId").GetGuid()))
                .ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Requests.Add(Parse(request).Select(m => m.Id).ToList());
            return Task.FromResult(Handler(request));
        }
    }

    private sealed class Link : IByteLink
    {
        public Channel<byte[]> Incoming { get; } = Channel.CreateUnbounded<byte[]>();

        public string Description => "giả";

        public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var chunk = await Incoming.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoLog : IRawLogStore
    {
        public string RootDirectory => "rawlog";

        public void Append(Guid instrumentId, RawLogEntry entry)
        {
        }

        public IReadOnlyList<DateOnly> ListDates(Guid instrumentId) => [];

        public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date) => [];

        public string FilePath(Guid instrumentId, DateOnly date) => "rawlog";
    }

    private sealed class NoQueries : IOrderQueryService
    {
        public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}

/// <summary>Báo lên server số bản tin của máy không còn gán (<c>pendingOrphan</c>) và hạn HTTP riêng từng yêu cầu.</summary>
public sealed class OrphanReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-orphan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static RawMessage Msg(Guid machine, long seq) =>
        new(Guid.CreateVersion7(), machine, seq, "p", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [1, 2], false);

    [Fact]
    public async Task Dem_ban_tin_cua_may_khong_con_gan_cho_agent()
    {
        var queue = new RawMessageQueue(_root);
        var gone = Guid.NewGuid();
        var gone2 = Guid.NewGuid();
        for (var seq = 1; seq <= 3; seq++)
        {
            await queue.SaveAsync(Msg(gone, seq), TestContext.Current.CancellationToken);
        }

        await queue.SaveAsync(Msg(gone2, 1), TestContext.Current.CancellationToken);
        var worker = new AgentWorker(
            null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentWorker>.Instance, TimeProvider.System,
            null!, null!, queue, null!);

        worker.OrphanMessageCount().ShouldBe(4); // chưa có máy nào gán cho agent
    }

    [Fact]
    public async Task Status_gui_kem_pendingOrphan_va_khong_lam_lo_khoa_agent()
    {
        string? body = null;
        var handler = new CaptureHandler(r => body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var http = ServerClient.CreateHttp("http://may-chu.thu", "khoa-bi-mat-123");
        http.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        var client = new ServerClient(new HttpClient(handler) { BaseAddress = http.BaseAddress });

        await client.SendStatusAsync([], TestContext.Current.CancellationToken, pendingOrphan: 7);

        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("pendingOrphan").GetInt32().ShouldBe(7);
        body!.ShouldNotContain("khoa-bi-mat-123");
        ServerClient.RawMessagesTimeout.ShouldBe(TimeSpan.FromSeconds(120));
        ServerClient.RequestTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private sealed class CaptureHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
