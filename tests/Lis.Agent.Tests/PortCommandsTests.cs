using System.Text.Json;
using Lis.Connections;
using Lis.Core.Ingest;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Core.Drivers;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class PortCommandsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _root = Directory.CreateTempSubdirectory("lis-port-cmd-").FullName;
    private readonly List<InstrumentConnectionManager> _managers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            manager.StopAllAsync().GetAwaiter().GetResult();
        }

        Directory.Delete(_root, recursive: true);
    }

    private sealed class IdleLink : IByteLink
    {
        public string Description => "giả";

        public Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoSink : IReadingSink
    {
        public Task<IngestOutcome> IngestAsync(InstrumentReading reading) => Task.FromResult(new IngestOutcome(0, 0, 0, 0));
    }

    private sealed class NoQueries : IOrderQueryService
    {
        public Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeSerial : ISerialPortService
    {
        public List<SerialPortEntry> Ports { get; } = [];

        public Func<ConnectionSettings, TimeSpan, CancellationToken, Task<byte[]>> OnListen { get; set; } = (_, _, _) => Task.FromResult<byte[]>([]);

        public List<ConnectionSettings> Calls { get; } = [];

        public IReadOnlyList<SerialPortEntry> ListPorts() => Ports;

        public Task<byte[]> ListenAsync(ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct)
        {
            Calls.Add(settings);
            return OnListen(settings, duration, ct);
        }
    }

    private sealed class FakeProbe(params string[] busy) : IPortProbe
    {
        public List<string> Probed { get; } = [];

        public bool IsBusy(string portName)
        {
            Probed.Add(portName);
            return busy.Contains(portName, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static InstrumentInfo Machine(string name, string port, string workstation = "WS1") =>
        new(Guid.NewGuid(), name, null, "astm-e1394", null,
            $$"""{"type":"serial","port":"{{port}}","baudRate":9600,"dataBits":8,"parity":"None","stopBits":1}""", workstation, true);

    private sealed class Rig
    {
        public required InstrumentConnectionManager Manager { get; init; }

        public required AgentRuntime Runtime { get; init; }

        public int LinksCreated;

        public required InstrumentInfo Machine { get; init; }
    }

    private async Task<Rig> StartAsync(params InstrumentInfo[] instruments)
    {
        Rig? rig = null;
        var manager = new InstrumentConnectionManager(
            () => Task.FromResult<IReadOnlyList<InstrumentInfo>>(instruments), "WS1", new RawLogStore(Path.Combine(_root, "raw")),
            new NoSink(), new NoQueries(), Path.Combine(_root, "pending"), TimeProvider.System,
            _ =>
            {
                Interlocked.Increment(ref rig!.LinksCreated);
                return new IdleLink();
            });
        _managers.Add(manager);
        var runtime = new AgentRuntime { Manager = manager, WorkstationCode = "WS1", Instruments = instruments };
        rig = new Rig { Manager = manager, Runtime = runtime, Machine = instruments.FirstOrDefault() ?? Machine("-", "COM99") };
        await manager.ReloadAsync();
        return rig;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "điều kiện không đạt trong thời hạn");
            await Task.Delay(20, Ct);
        }
    }

    private static JsonElement Args(string port = "COM3", int seconds = 5) =>
        JsonSerializer.SerializeToElement(new { portName = port, baudRate = 9600, dataBits = 8, parity = "None", stopBits = 1, seconds });

    [Fact]
    public async Task Thu_cong_dang_dung_tra_lai_cong_ca_khi_loi()
    {
        var machine = Machine("Máy Thử", "COM3");
        var rig = await StartAsync(machine);
        await Eventually(() => rig.LinksCreated == 1);
        var serial = new FakeSerial
        {
            OnListen = (s, _, _) => throw new SerialPortBusyException(s.PortName!, new UnauthorizedAccessException()),
        };

        var result = await new TestPortHandler(rig.Runtime, serial).RunAsync(Args(), Ct);

        result!.Value.GetProperty("opened").GetBoolean().ShouldBeFalse();
        result.Value.GetProperty("error").GetString()!.ShouldContain("COM3");
        result.Value.GetProperty("borrowedFrom").EnumerateArray().Select(e => e.GetString()).ShouldBe(["Máy Thử"]);
        await Eventually(() => rig.LinksCreated == 2);
        await Eventually(() => rig.Manager.StatusOf(machine.Id).State != LinkState.NotRunning);
    }

    [Fact]
    public async Task Thu_cong_bi_huy_giua_chung_van_tra_lai_cong()
    {
        var machine = Machine("Máy Thử", "COM3");
        var rig = await StartAsync(machine);
        await Eventually(() => rig.LinksCreated == 1);
        var started = new TaskCompletionSource();
        var serial = new FakeSerial
        {
            OnListen = async (_, _, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = new TestPortHandler(rig.Runtime, serial).RunAsync(Args(seconds: 60), cts.Token);
        await started.Task.WaitAsync(Wait, Ct);
        await Eventually(() => rig.Manager.StatusOf(machine.Id).Text == InstrumentConnectionManager.BorrowedStatusText);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
        await Eventually(() => rig.LinksCreated == 2);
    }

    [Fact]
    public async Task Thu_cong_loi_la_cung_tra_lai_cong()
    {
        var rig = await StartAsync(Machine("Máy Thử", "COM3"));
        await Eventually(() => rig.LinksCreated == 1);
        var serial = new FakeSerial { OnListen = (_, _, _) => throw new InvalidOperationException("hỏng") };

        var result = await new TestPortHandler(rig.Runtime, serial).RunAsync(Args(), Ct);

        result!.Value.GetProperty("opened").GetBoolean().ShouldBeFalse();
        await Eventually(() => rig.LinksCreated == 2);
    }

    [Fact]
    public async Task Reload_khong_mo_may_dang_bi_muon()
    {
        var machine = Machine("Máy Thử", "COM3");
        var rig = await StartAsync(machine);
        await Eventually(() => rig.LinksCreated == 1);

        var lease = await rig.Manager.BorrowPortAsync("com3", Ct);
        lease.InstrumentNames.ShouldBe(["Máy Thử"]);
        rig.Manager.StatusOf(machine.Id).Text.ShouldBe("Đang nhường cổng cho lệnh thử cổng");
        await rig.Manager.ReloadAsync();
        await rig.Manager.RestartAsync(machine.Id);
        await Task.Delay(200, Ct);
        rig.LinksCreated.ShouldBe(1);

        await lease.DisposeAsync();
        await lease.DisposeAsync(); // lần hai không làm gì
        await Eventually(() => rig.LinksCreated == 2);
    }

    [Fact]
    public async Task Muon_hai_cong_khac_nhau_cung_luc_va_cong_khong_ai_dung()
    {
        var a = Machine("Máy Thử A", "COM3");
        var b = Machine("Máy Thử B", "COM4");
        var rig = await StartAsync(a, b);
        await Eventually(() => rig.LinksCreated == 2);

        await using var l3 = await rig.Manager.BorrowPortAsync("COM3", Ct);
        await using var l4 = await rig.Manager.BorrowPortAsync("COM4", Ct);
        await using var l9 = await rig.Manager.BorrowPortAsync("COM9", Ct);

        l3.InstrumentNames.ShouldBe(["Máy Thử A"]);
        l4.InstrumentNames.ShouldBe(["Máy Thử B"]);
        l9.InstrumentNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task Quet_cong()
    {
        var rig = await StartAsync(Machine("Máy Thử", "COM3"));
        var serial = new FakeSerial { Ports = { new("COM3", "USB-SERIAL CH340"), new("COM5", null), new("COM7", "Cổng Mẫu") } };
        var probe = new FakeProbe("COM5");

        var result = await new ScanPortsHandler(rig.Runtime, serial, probe).RunAsync(null, Ct);

        var ports = result!.Value.GetProperty("ports").EnumerateArray().ToList();
        ports.Select(p => p.GetProperty("name").GetString()).ShouldBe(["COM3", "COM5", "COM7"]);
        ports.Select(p => p.GetProperty("usedBy").GetString()).ShouldBe(["Máy Thử", null, null]);
        ports.Select(p => p.GetProperty("busy").GetBoolean()).ShouldBe([false, true, false]);
        ports[0].GetProperty("description").GetString().ShouldBe("USB-SERIAL CH340");
        ports[1].GetProperty("description").ValueKind.ShouldBe(JsonValueKind.Null);
        probe.Probed.ShouldBe(["COM5", "COM7"]);
    }

    [Fact]
    public async Task Thu_cong_tra_hex_toi_da_64KB()
    {
        var rig = await StartAsync();
        var serial = new FakeSerial { OnListen = (_, _, _) => Task.FromResult(Enumerable.Repeat((byte)0xAB, 70_000).ToArray()) };

        var result = await new TestPortHandler(rig.Runtime, serial).RunAsync(Args(), Ct);

        result!.Value.GetProperty("opened").GetBoolean().ShouldBeTrue();
        result.Value.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        result.Value.GetProperty("byteCount").GetInt32().ShouldBe(70_000);
        result.Value.GetProperty("hex").GetString()!.Length.ShouldBe(131_072);
        result.Value.GetProperty("borrowedFrom").GetArrayLength().ShouldBe(0);
        serial.Calls.Single().ShouldBe(new ConnectionSettings { Kind = ConnectionKind.Serial, PortName = "COM3" }, new SettingsComparer());
    }

    [Fact]
    public async Task Thu_cong_hex_chu_hoa()
    {
        var rig = await StartAsync();
        var serial = new FakeSerial { OnListen = (_, _, _) => Task.FromResult<byte[]>([0x05, 0xAB, 0xFF]) };

        var result = await new TestPortHandler(rig.Runtime, serial).RunAsync(Args(), Ct);

        result!.Value.GetProperty("hex").GetString().ShouldBe("05ABFF");
    }

    private sealed class SettingsComparer : IEqualityComparer<ConnectionSettings>
    {
        public bool Equals(ConnectionSettings? x, ConnectionSettings? y) =>
            x!.Kind == y!.Kind && x.PortName == y.PortName && x.BaudRate == y.BaudRate && x.DataBits == y.DataBits
            && x.Parity == y.Parity && x.StopBits == y.StopBits;

        public int GetHashCode(ConnectionSettings obj) => 0;
    }

    [Theory]
    [InlineData("/dev/tty", 9600, 8, "None", 1, 5)]
    [InlineData("COM3", 100, 8, "None", 1, 5)]
    [InlineData("COM3", 9600, 9, "None", 1, 5)]
    [InlineData("COM3", 9600, 8, "Mark", 1, 5)]
    [InlineData("COM3", 9600, 8, "None", 3, 5)]
    [InlineData("COM3", 9600, 8, "None", 1, 61)]
    [InlineData("COM3", 9600, 8, "None", 1, 0)]
    public async Task Thu_cong_args_sai_bao_loi_va_khong_muon_cong(string port, int baud, int dataBits, string parity, int stopBits, int seconds)
    {
        var rig = await StartAsync(Machine("Máy Thử", "COM3"));
        await Eventually(() => rig.LinksCreated == 1);
        var args = JsonSerializer.SerializeToElement(new { portName = port, baudRate = baud, dataBits, parity, stopBits, seconds });

        await Should.ThrowAsync<AgentCommandException>(() => new TestPortHandler(rig.Runtime, new FakeSerial()).RunAsync(args, Ct));
        rig.Manager.StatusOf(rig.Machine.Id).Text.ShouldNotBe(InstrumentConnectionManager.BorrowedStatusText);
    }

    [Fact]
    public async Task Thu_cong_thieu_args_bao_loi()
    {
        var rig = await StartAsync();
        await Should.ThrowAsync<AgentCommandException>(() => new TestPortHandler(rig.Runtime, new FakeSerial()).RunAsync(null, Ct));
        await Should.ThrowAsync<AgentCommandException>(
            () => new TestPortHandler(rig.Runtime, new FakeSerial()).RunAsync(JsonSerializer.SerializeToElement(new { portName = "COM3" }), Ct));
    }
}
