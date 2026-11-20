using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dapper;
using Lis.Agent;
using Lis.Connections;
using Lis.Connections.RawMessages;
using Lis.Core.Drivers.BeckmanAu;
using Lis.Core.Instruments;
using Lis.Data;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Orders;
using Lis.Simulator.Links;
using Lis.Simulator.Messages;
using Lis.Simulator.Sessions;
using Lis.Server.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.EndToEnd;

/// <summary>Một họ máy thử: giao thức, mã kênh của WBC / HGB / PLT, một mã kênh không map, và phần kết quả thêm (AU nhiều khối).</summary>
internal sealed record Family(
    string Name, string Protocol, string? Model, string? DriverConfigJson,
    string Wbc, string Hgb, string Plt, string Unmapped,
    BeckmanAuOptions? Au = null, IReadOnlyList<SimResult>? Extra = null, bool HasChecksum = false)
{
    public bool IsAstm => Protocol == "astm-e1394";

    public bool IsAu => Protocol == "beckman-au";

    public bool IsHl7 => Protocol == "hl7-mllp";

    private static readonly BeckmanAuOptions AuBlocks = new() { ClassB = true, Bcc = true, TextLength = 256 };

    public static IReadOnlyList<Family> All { get; } =
    [
        new("hl7", "hl7-mllp", "BC-5380", null, "6690-2", "718-7", "777-3", "999-9"),
        new("astm-generic", "astm-e1394", "Máy ASTM thử", null, "WBC", "HGB", "PLT", "ZZZ", HasChecksum: true),
        new("astm-access2", "astm-e1394", "Access 2 thử", null, "WBC", "HGB", "PLT", "ZZZ", HasChecksum: true),
        new("au480", "beckman-au", "AU480", null, "01", "02", "03", "77", BeckmanAuOptions.Au480),
        new("au480-nhieu-khoi", "beckman-au", "AU480", """{"classB":true,"bcc":true,"textLength":256}""", "01", "02", "03", "77", AuBlocks,
            Extra: Enumerable.Range(10, 10).Select(i => new SimResult(i.ToString("00"), (i - 9).ToString())).ToList(), HasChecksum: true),
    ];

    public static Family Named(string name) => All.Single(f => f.Name == name);

    public static IEnumerable<object[]> Names => All.Select(f => new object[] { f.Name });

    public static IEnumerable<object[]> NamesWithChecksum => All.Where(f => f.HasChecksum).Select(f => new object[] { f.Name });
}

/// <summary>Đường nối trong bộ nhớ giữa "máy" (bộ giả lập) và runner: byte máy ghi vào runner đọc, byte runner ghi máy đọc.</summary>
internal sealed class Wire
{
    private readonly Channel<byte[]> _toRunner = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _toMachine = Channel.CreateUnbounded<byte[]>();

    public Wire()
    {
        Runner = new RunnerSide(this);
        Machine = new MachineSide(this);
    }

    public IByteLink Runner { get; }

    public IMachineLink Machine { get; }

    private sealed class RunnerSide(Wire wire) : IByteLink
    {
        private byte[]? _rest;
        private int _offset;

        public string Description => "dây thử";

        public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_rest is null)
            {
                var chunk = await wire._toRunner.Reader.ReadAsync(ct);
                if (chunk.Length == 0)
                {
                    return 0;
                }

                _rest = chunk;
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _rest.Length - _offset);
            _rest.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            if (_offset == _rest.Length)
            {
                _rest = null;
            }

            return n;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            wire._toMachine.Writer.WriteAsync(data.ToArray(), ct).AsTask();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MachineSide(Wire wire) : IMachineLink
    {
        public Task WriteAsync(byte[] data, CancellationToken ct) => wire._toRunner.Writer.WriteAsync(data, ct).AsTask();

        public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct)
        {
            var bytes = new List<byte>();
            using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                first.CancelAfter(timeout);
                try
                {
                    bytes.AddRange(await wire._toMachine.Reader.ReadAsync(first.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return [];
                }
            }

            while (true)
            {
                using var more = CancellationTokenSource.CreateLinkedTokenSource(ct);
                more.CancelAfter(TimeSpan.FromMilliseconds(30));
                try
                {
                    bytes.AddRange(await wire._toMachine.Reader.ReadAsync(more.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return [.. bytes];
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Bọc đường nối của máy để sửa byte đi (một lần): dùng cho ca checksum / BCC sai.</summary>
internal sealed class TamperLink(IMachineLink inner) : IMachineLink
{
    public Func<byte[], byte[]>? Tamper { get; set; }

    public Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (Tamper is { } tamper)
        {
            var changed = tamper(data);
            if (!ReferenceEquals(changed, data))
            {
                Tamper = null;
                data = changed;
            }
        }

        return inner.WriteAsync(data, ct);
    }

    public Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct) => inner.ReadAsync(timeout, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>"Máy" xét nghiệm thử: dựng bản tin bằng Lis.Simulator và nói chuyện với runner qua <see cref="Wire"/>.</summary>
internal sealed class Machine
{
    private static readonly SessionTimings Timings = new()
    {
        ClassAGap = TimeSpan.FromMilliseconds(20), Reply = TimeSpan.FromSeconds(15), QueryAnswer = TimeSpan.FromSeconds(20),
    };

    private readonly Family _family;
    private readonly TamperLink _link;
    private readonly MachineSession _session;
    private readonly string _label;
    private int _stamp;

    public Machine(Family family, Wire wire, string label)
    {
        _family = family;
        _label = label;
        _link = new TamperLink(wire.Machine);
        _session = new MachineSession(_link, _ => { }, Timings);
    }

    public void TamperNextFrame(Func<byte[], byte[]> tamper) => _link.Tamper = tamper;

    /// <summary>Công thức máu 3 chỉ số theo mã kênh của họ này (cộng phần kết quả thêm nếu có).</summary>
    public SimResult[] Cbc(string wbc = "7.5", string hgb = "135", string plt = "250", params SimResult[] extra) =>
        [new(_family.Wbc, wbc), new(_family.Hgb, hgb), new(_family.Plt, plt), .. _family.Extra ?? [], .. extra];

    public async Task SendAsync(string barcode, SimResult[]? results = null, bool qc = false)
    {
        _ = ++_stamp;
        var list = results ?? Cbc();
        var ct = TestContext.Current.CancellationToken;
        var result = _family switch
        {
            { IsHl7: true } => await _session.SendHl7ResultAsync(barcode, list, qc, ct),
            { IsAstm: true } => await _session.SendAstmResultAsync(barcode, list, qc, ct),
            _ => await _session.SendAuResultAsync(_family.Au!, barcode, list, qc, ct),
        };
        result.Ok.ShouldBeTrue($"{_family.Name} {barcode} (máy {_label}): {result.Message}");
    }

    /// <summary>Gửi và trả (không ném) kết quả phiên — dùng cho ca bản tin hỏng (máy có thể bị NAK).</summary>
    public async Task<SessionResult> TrySendAsync(string barcode, SimResult[]? results = null)
    {
        var list = results ?? Cbc();
        var ct = TestContext.Current.CancellationToken;
        return _family switch
        {
            { IsHl7: true } => await _session.SendHl7ResultAsync(barcode, list, false, ct),
            { IsAstm: true } => await _session.SendAstmResultAsync(barcode, list, false, ct),
            _ => await _session.SendAuResultAsync(_family.Au!, barcode, list, false, ct),
        };
    }

    /// <summary>Máy hỏi chỉ định; trả phần LIS trả lời đã bỏ các số dài (giờ, mã tin) để so hai đường.</summary>
    public async Task<string> QueryAsync(string barcode)
    {
        var ct = TestContext.Current.CancellationToken;
        if (_family.IsHl7)
        {
            var orm = Hl7ResultBuilder.Frame(
                "MSH|^~\\&|Analyzer|Mindray|||20261120090000||ORM^O01|4|P|2.3.1||||||UNICODE\r" + $"ORC|RF||{barcode}||IP\r");
            await _link.WriteAsync(orm, ct);
            var reply = new List<byte>();
            while (!reply.Contains(Hl7ResultBuilder.EndBlock))
            {
                var data = await _link.ReadAsync(TimeSpan.FromSeconds(20), ct);
                data.Length.ShouldBeGreaterThan(0, "LIS không trả lời câu hỏi chỉ định HL7");
                reply.AddRange(data);
            }

            var ack = Hl7ResultBuilder.Frame("MSH|^~\\&|Analyzer|Mindray|||20261120090001||ACK^O02|11|P|2.3.1\rMSA|AA|4\r");
            await _link.WriteAsync(ack, ct);
            await _link.ReadAsync(TimeSpan.FromMilliseconds(300), ct); // driver HL7 trả AR cho cả ACK của máy; máy thật bỏ qua, ở đây đọc bỏ để khỏi lẫn vào phiên sau
            return Normalize(Encoding.UTF8.GetString([.. reply]));
        }

        var result = _family.IsAstm
            ? await _session.QueryAstmAsync(barcode, ct)
            : await _session.QueryAuAsync(_family.Au!, barcode, ct);
        result.Ok.ShouldBeTrue(result.Message);
        return Normalize(string.Join("\n", result.Lines));
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\d{8,}", "#");
}

/// <summary>Cầu nối giả cho <see cref="IRawLogStore"/>: bỏ log thô (test này không đọc).</summary>
internal sealed class NullRawLogs : IRawLogStore
{
    public string RootDirectory => "";

    public void Append(Guid instrumentId, RawLogEntry entry)
    {
    }

    public IReadOnlyList<DateOnly> ListDates(Guid instrumentId) => [];

    public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date) => [];

    public string FilePath(Guid instrumentId, DateOnly date) => "";
}

/// <summary>
/// Đường HTTP agent → server thử, có công tắc: server tắt (ném lỗi mạng), hoặc server đã xử lý lô nhưng phản hồi bị mất.
/// <see cref="Target"/> null nghĩa là server đang tắt.
/// </summary>
internal sealed class SwitchHandler : HttpMessageHandler
{
    private int _attempts;
    private int _loseResponses;

    public HttpClient? Target { get; set; }

    /// <summary>Mất mạng: mọi lần gọi hỏng ngay, server (nếu còn chạy) không nhận được gì.</summary>
    public volatile bool Down;

    /// <summary>Số lần đã thử gọi (kể cả lần hỏng).</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Số lần gọi tới <c>raw-messages</c> bị hỏng vì server tắt.</summary>
    public int Failures { get; private set; }

    /// <summary>Lần gọi tiếp theo tới <c>raw-messages</c>: server xử lý xong nhưng phản hồi không về tới agent.</summary>
    public void LoseNextResponses(int count) => Interlocked.Exchange(ref _loseResponses, count);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        if (Down || Target is not { } target)
        {
            Failures++;
            throw new HttpRequestException("Máy chủ đang tắt");
        }

        // HttpClient đã đánh dấu yêu cầu này là "đã gửi": chuyển bản sao sang client của server thử.
        using var forward = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            forward.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is { } content)
        {
            forward.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(ct));
            foreach (var header in content.Headers)
            {
                forward.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var response = await target.SendAsync(forward, ct);
        if (request.RequestUri!.AbsolutePath.EndsWith("raw-messages", StringComparison.Ordinal) && Interlocked.Decrement(ref _loseResponses) >= 0)
        {
            response.Dispose();
            Failures++;
            throw new HttpRequestException("Mất phản hồi giữa đường");
        }

        return response;
    }
}

/// <summary>Một phòng khám trong cặp đối chiếu: dịch vụ, máy, mẫu đã chỉ định.</summary>
internal sealed class Side(Clinic clinic, TenantServices tenant)
{
    public const string Sentinel = "9261120007";

    public Clinic Clinic { get; } = clinic;

    public TenantServices Tenant { get; } = tenant;

    public Guid InstrumentId { get; set; }

    public Guid ActorId { get; set; }

    /// <summary>Mã vạch → id mẫu.</summary>
    public Dictionary<string, Guid> Samples { get; } = [];

    public Db Db => Tenant.Db;

    /// <summary>Danh mục, máy (map kênh, khai dịch vụ chạy được) và các mẫu đã chỉ định WBC + HGB + PLT.</summary>
    public async Task SeedAsync(Family family, string workstation, params string[] barcodes)
    {
        await new FacilityStore(Db).SaveAsync(new FacilityInfo("Phòng xét nghiệm Thử", null, null, null, "9", "yyMMdd", 3, "single"));
        var catalog = new CatalogStore(Db);
        var edta = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var wbc = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "WBC", "Bạch cầu", null, edta, "G/L", "so", 2, 0, false, false, true, null, []));
        var hgb = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "HGB", "Huyết sắc tố", null, edta, "g/L", "so", 0, 0, false, false, true, null, []));
        var plt = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "PLT", "Tiểu cầu", null, edta, "G/L", "so", 0, 0, false, false, true, null, []));
        var actor = await Tenant.Users.CreateAsync("ktv.thu", "Kỹ thuật viên Thử", null, RawEnv.Password, ["ky-thuat-vien", "truong-khoa"]);
        ActorId = actor.Id;

        var instruments = new InstrumentStore(Db);
        InstrumentId = await instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, $"Máy thử {family.Name}", family.Model, family.Protocol, family.DriverConfigJson,
            """{"type":"tcp","role":"server","port":5100}""", workstation, true));
        foreach (var (code, service) in new[] { (family.Wbc, wbc), (family.Hgb, hgb), (family.Plt, plt) })
        {
            await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, InstrumentId, code, service, null, null, false, true));
            await instruments.SetDeclaredAsync(InstrumentId, service, true);
        }

        var orders = new OrderService(Db, TimeProvider.System);
        var number = 0;
        foreach (var barcode in barcodes)
        {
            var created = await orders.CreateAsync(
                new NewOrder(null, new NewPatient($"Nguyễn Văn Mẫu {++number}", null, 1980, 'M', null, null), [wbc, hgb, plt], null, null, null), actor.Id);
            var sample = created.Samples.Single();
            await using var connection = await Db.OpenAsync();
            await connection.ExecuteAsync("UPDATE sample SET barcode = @barcode WHERE id = @id", new { barcode, id = sample.SampleId });
            Samples[barcode] = sample.SampleId;
        }
    }

    /// <summary>Kết quả theo mẫu + chỉ số: giá trị, cờ, trạng thái, giá trị chờ thay, số dòng lịch sử.</summary>
    public async Task<IReadOnlyList<string>> ResultsAsync()
    {
        await using var connection = await Db.OpenAsync();
        return (await connection.QueryAsync<string>(
            """
            SELECT concat_ws('|', s.barcode, sv.code, coalesce(r.value, '~'), coalesce(r.flag, '~'), r.status,
                             coalesce(r.pending_value, '~'), (SELECT count(*) FROM result_history h WHERE h.result_id = r.id))
            FROM result r
            JOIN order_item oi ON oi.id = r.order_item_id
            JOIN sample s ON s.id = oi.sample_id
            JOIN service sv ON sv.id = oi.service_id
            ORDER BY s.barcode, sv.code
            """)).ToList();
    }

    public async Task<IReadOnlyList<string>> QcAsync()
    {
        await using var connection = await Db.OpenAsync();
        return (await connection.QueryAsync<string>(
            "SELECT concat_ws('|', barcode, channel_code, value) FROM qc_result ORDER BY barcode, channel_code, value")).ToList();
    }

    public async Task<IReadOnlyList<string>> UnmatchedAsync()
    {
        await using var connection = await Db.OpenAsync();
        return (await connection.QueryAsync<string>(
            "SELECT concat_ws('|', barcode, channel_code, value, reason) FROM unmatched_result ORDER BY barcode, channel_code, value, reason")).ToList();
    }

    public async Task<bool> HasValueAsync(string barcode)
    {
        await using var connection = await Db.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM result r JOIN order_item oi ON oi.id = r.order_item_id JOIN sample s ON s.id = oi.sample_id
                           WHERE s.barcode = @barcode AND r.value IS NOT NULL)
            """, new { barcode });
    }
}

/// <summary>
/// Cặp phòng khám để đối chiếu: A chạy đường cũ (<c>InstrumentRunner</c> + <c>IngestReadingSink</c>), B chạy đường mới
/// (runner chế độ bản tin thô + hàng chờ đĩa + <see cref="RawMessageUploader"/> + server thật giải mã). Hai phòng khám cùng danh mục,
/// máy, map kênh và chỉ định. Mọi bước máy gửi được lặp y hệt ở hai bên.
/// </summary>
internal sealed class PipelineRig : IAsyncDisposable
{
    public static readonly string[] Barcodes =
        ["9261120001", "9261120002", "9261120003", "9261120004", "9261120005", "9261120006", Side.Sentinel, "9261120008", "9261120009", "9261120010"];

    private readonly ServerFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<ApiHost> _servers = [];
    private ApiHost? _server;
    private InstrumentConnectionManager _managerA = null!;
    private InstrumentConnectionManager _managerB = null!;
    private int _closed;

    private PipelineRig(ServerFixture fixture, Family family)
    {
        _fixture = fixture;
        Family = family;
    }

    public Family Family { get; }

    public ApiHost Reference { get; private set; } = null!;

    public Side A { get; private set; } = null!;

    public Side B { get; private set; } = null!;

    public Machine MachineA { get; private set; } = null!;

    public Machine MachineB { get; private set; } = null!;

    public RawMessageQueue Queue { get; private set; } = null!;

    public RawMessageUploader Uploader { get; private set; } = null!;

    public FakeTimeProvider UploaderClock { get; } = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(7)));

    public SwitchHandler Switch { get; } = new();

    /// <summary>Số bản tin runner phía B đã ghi xuống đĩa.</summary>
    public int Saved => _counting?.Saved ?? 0;

    private CountingSink? _counting;

    public static async Task<PipelineRig> CreateAsync(ServerFixture fixture, Family family, string tag)
    {
        var rig = new PipelineRig(fixture, family);
        await rig.StartAsync(tag);
        return rig;
    }

    private async Task StartAsync(string tag)
    {
        Directory.CreateDirectory(_root);
        Reference = await ApiHost.StartAsync(_fixture);
        var clinicA = await Reference.AddClinicAsync($"pa{tag}", "Phòng khám Thử A");
        var clinicB = await Reference.AddClinicAsync($"pb{tag}", "Phòng khám Thử B");
        A = new Side(clinicA, Reference.TenantOf(clinicA));
        B = new Side(clinicB, Reference.TenantOf(clinicB));
        await A.SeedAsync(Family, "XN-01", Barcodes);
        await B.SeedAsync(Family, "XN-01", Barcodes);
        var (_, key) = await Reference.AddAgentWithKeyAsync(clinicB, "Agent thử", "XN-01");

        await StartServerAsync();
        var http = new HttpClient(Switch) { BaseAddress = new Uri("http://may-chu.thu/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add(ServerClient.KeyHeader, key);
        var client = new ServerClient(http);

        var wireA = new Wire();
        var wireB = new Wire();
        MachineA = new Machine(Family, wireA, "A đường cũ");
        MachineB = new Machine(Family, wireB, "B đường mới");

        _managerA = new InstrumentConnectionManager(
            A.Tenant.Instruments.ListAsync, "XN-01", new NullRawLogs(), new IngestReadingSink(A.Tenant.Ingest, A.Tenant.Learning),
            A.Tenant.OrderQueries, Path.Combine(_root, "cho-ghi-a"), TimeProvider.System, _ => wireA.Runner);

        Queue = new RawMessageQueue(Path.Combine(_root, "ban-tin"));
        _counting = new CountingSink(Queue);
        _managerB = new InstrumentConnectionManager(
            () => client.InstrumentsAsync(), "XN-01", new NullRawLogs(), new RawModeReadingSink(),
            new HttpOrderQueries(client), Path.Combine(_root, "cho-ghi-b"), TimeProvider.System, _ => wireB.Runner, _counting);
        Uploader = new RawMessageUploader(Queue, client, UploaderClock, NullLogger.Instance);

        await _managerA.ReloadAsync();
        await _managerB.ReloadAsync();
    }

    /// <summary>Bật (lại) server thử cho phòng khám B; đường HTTP của agent trỏ sang server mới.</summary>
    public async Task StartServerAsync()
    {
        _server = await ApiHost.StartAsync(_fixture);
        _servers.Add(_server);
        Switch.Target = _server.AgentClient();
    }

    /// <summary>Tắt hẳn server thử (huỷ cả host): agent gửi sẽ gặp lỗi mạng.</summary>
    public async Task StopServerAsync()
    {
        Switch.Target = null;
        if (_server is { } server)
        {
            _server = null;
            _servers.Remove(server);
            await server.DisposeAsync();
        }
    }

    /// <summary>Chạy cùng một bước máy gửi ở cả hai phòng khám (A trước, B sau).</summary>
    public async Task BothAsync(Func<Machine, Task> step)
    {
        await step(MachineA);
        await step(MachineB);
    }

    /// <summary>Máy hỏi chỉ định ở cả hai bên; câu trả lời của LIS phải như nhau.</summary>
    public async Task QueryBothAsync(string barcode)
    {
        var a = await MachineA.QueryAsync(barcode);
        var b = await MachineB.QueryAsync(barcode);
        b.ShouldBe(a, $"câu trả lời chỉ định cho {barcode} khác nhau giữa đường cũ và đường mới");
    }

    /// <summary>Gửi mẫu chốt (kết quả mới nhất của máy) rồi đợi nó có ở A (đường cũ) — dấu hiệu mọi bản tin trước đó đã xử lý.</summary>
    public async Task FinishAsync(Func<Task>? driveUploader = null)
    {
        await BothAsync(m => m.SendAsync(Side.Sentinel));
        await WaitAsync(() => A.HasValueAsync(Side.Sentinel), "mẫu chốt ở đường cũ");
        await DrainAsync(Side.Sentinel, driveUploader);
    }

    /// <summary>Đợi runner phía B ngừng ghi bản tin mới xuống đĩa (ASTM / AU Class A không có ACK cuối để làm mốc).</summary>
    public async Task SettleAsync()
    {
        var last = -1;
        var since = DateTime.UtcNow;
        while (true)
        {
            var now = Saved;
            if (now != last)
            {
                last = now;
                since = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - since > TimeSpan.FromMilliseconds(400))
            {
                return;
            }

            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Đẩy hàng chờ của agent tới khi mẫu chốt có ở B (bỏ lùi nhịp, như lệnh gửi ngay).</summary>
    public async Task DrainAsync(string barcode = Side.Sentinel, Func<Task>? driveUploader = null)
    {
        await WaitAsync(async () =>
        {
            if (driveUploader is not null)
            {
                await driveUploader();
            }
            else
            {
                await Uploader.FlushNowAsync(TestContext.Current.CancellationToken);
            }

            return await B.HasValueAsync(barcode);
        }, $"mẫu {barcode} ở đường mới");
        // Mẫu chốt có rồi thì mọi bản tin trước nó đã qua server (thứ tự gửi theo Seq); dọn nốt phần còn lại của hàng.
        await WaitAsync(async () =>
        {
            await Uploader.FlushNowAsync(TestContext.Current.CancellationToken);
            return Queue.Count() == 0;
        }, "hàng chờ agent rỗng");
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds = 60)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException($"Hết giờ chờ: {what}");
            }

            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Hai phòng khám phải có cùng dữ liệu (theo mã vạch + chỉ số, bỏ id / thời điểm / raw_ref).</summary>
    public async Task AssertEquivalentAsync()
    {
        (await B.ResultsAsync()).ShouldBe(await A.ResultsAsync(), "result (giá trị, cờ, trạng thái, chờ thay, số dòng lịch sử)");
        (await B.QcAsync()).ShouldBe(await A.QcAsync(), "qc_result");
        (await B.UnmatchedAsync()).ShouldBe(await A.UnmatchedAsync(), "unmatched_result");
    }

    /// <summary>Số dòng <c>raw_message</c> ở B (mỗi bản tin agent ghi đĩa phải có đúng một dòng).</summary>
    public async Task<int> ServerMessagesAsync()
    {
        await using var connection = await B.Db.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM raw_message");
    }

    /// <summary>Trạng thái giải mã của các bản tin có chứa <paramref name="text"/> nhưng không chứa <paramref name="without"/>.</summary>
    public async Task<IReadOnlyList<string>> ServerStatusesAsync(string text, string without)
    {
        await using var connection = await B.Db.OpenAsync();
        return (await connection.QueryAsync<string>(
            """
            SELECT decode_status FROM raw_message
            WHERE position(convert_to(@text, 'UTF8') in bytes) > 0 AND position(convert_to(@without, 'UTF8') in bytes) = 0
            """,
            new { text, without })).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        try
        {
            await _managerA.StopAllAsync();
            await _managerB.StopAllAsync();
        }
        finally
        {
            foreach (var server in _servers)
            {
                await server.DisposeAsync();
            }

            await Reference.DisposeAsync();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class CountingSink(RawMessageQueue inner) : IRawMessageSink
    {
        private int _saved;

        public int Saved => Volatile.Read(ref _saved);

        public async Task SaveAsync(RawMessage message, CancellationToken ct)
        {
            await inner.SaveAsync(message, ct);
            Interlocked.Increment(ref _saved);
        }

        public long MaxSeq(Guid instrumentId) => inner.MaxSeq(instrumentId);
    }
}
