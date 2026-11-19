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

    private RawMessageUploader Uploader() =>
        new(_queue, new ServerClient(new HttpClient(_server) { BaseAddress = new Uri("http://may-chu.thu/") }), _clock, NullLogger.Instance);

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

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(120);
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

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(10);
        _queue.Peek(Machine, 100).Select(m => m.Seq).ShouldBe(Enumerable.Range(51, 10).Select(i => (long)i));
    }

    [Fact]
    public async Task Server_tra_trung_xoa_tep()
    {
        await AddAsync(1);
        _server.Handler = request => FakeServer.Reply(request, _ => "trung");

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
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

        (await Uploader().FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

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

        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
        _server.Calls.ShouldBe(1); // dừng ngay, không thử máy kế tiếp

        _clock.Advance(TimeSpan.FromHours(1));
        (await uploader.FlushNowAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
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

    private sealed class FakeServer : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = SavedAll;

        /// <summary>Mỗi phần tử = các Id trong một lần gọi, theo thứ tự gửi.</summary>
        public List<List<Guid>> Requests { get; } = [];

        public List<string> Urls { get; } = [];

        public List<string> Bodies { get; } = [];

        public int Calls { get; private set; }

        public static HttpResponseMessage SavedAll(HttpRequestMessage request) => Reply(request, _ => "da-luu");

        public static HttpResponseMessage Reply(HttpRequestMessage request, Func<(Guid Id, Guid InstrumentId), string> status)
        {
            var messages = Parse(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new { results = messages.Select(m => new { id = m.Id, status = status(m), decodeStatus = "da-giai-ma" }) },
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
        }

        private static List<(Guid Id, Guid InstrumentId)> Parse(HttpRequestMessage request)
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
