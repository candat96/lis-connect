using System.Text;
using Lis.App.Services;
using Lis.App.ViewModels.Instruments;
using Lis.Core.Auth;
using Lis.Core.Instruments;
using Lis.Data.Auth;
using Lis.Data.Instruments;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Cổng COM giả: danh sách cổng cố định, mỗi cổng trả sẵn một đoạn byte hoặc báo bận.</summary>
internal sealed class FakeSerialPortService : ISerialPortService
{
    public List<SerialPortEntry> Ports { get; } = [];

    public Dictionary<string, byte[]> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Busy { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ConnectionSettings> ListenCalls { get; } = [];

    public IReadOnlyList<SerialPortEntry> ListPorts() => Ports;

    public Task<byte[]> ListenAsync(ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct)
    {
        lock (ListenCalls)
        {
            ListenCalls.Add(settings);
        }

        if (Busy.Contains(settings.PortName!))
        {
            throw new SerialPortBusyException(settings.PortName!, new UnauthorizedAccessException());
        }

        var bytes = Data.GetValueOrDefault(settings.PortName!) ?? [];
        if (bytes.Length > 0)
        {
            onBytes?.Invoke(bytes);
        }

        return Task.FromResult(bytes);
    }
}

/// <summary>Log thô trong bộ nhớ.</summary>
internal sealed class FakeRawLogStore : IRawLogStore
{
    public Dictionary<Guid, List<RawLogEntry>> Entries { get; } = [];

    public string RootDirectory => "rawlog";

    public void Append(Guid instrumentId, RawLogEntry entry)
    {
        lock (Entries)
        {
            (Entries.TryGetValue(instrumentId, out var list) ? list : Entries[instrumentId] = []).Add(entry);
        }
    }

    public IReadOnlyList<DateOnly> ListDates(Guid instrumentId) =>
        Entries.TryGetValue(instrumentId, out var list)
            ? list.Select(e => DateOnly.FromDateTime(e.Timestamp.ToLocalTime().DateTime)).Distinct().OrderDescending().ToList()
            : [];

    public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date) =>
        Entries.TryGetValue(instrumentId, out var list)
            ? list.Where(e => DateOnly.FromDateTime(e.Timestamp.ToLocalTime().DateTime) == date).ToList()
            : [];

    public string FilePath(Guid instrumentId, DateOnly date) => $"rawlog/{instrumentId:N}/{date:yyyy-MM-dd}.listrace";
}

/// <summary>Form kết nối, dò cổng COM và ghi log thô khi nghe thử ở màn "Máy &amp; map".</summary>
public class InstrumentConnectionTests
{
    private static readonly byte[] AstmEnq = [0x05];

    private static InstrumentsViewModel Create(FakeInstrumentStore store, FakeSerialPortService serial, FakeRawLogStore? logs = null)
    {
        var user = new UserInfo(Guid.NewGuid(), "qt.thu", "Quản trị Thử", null, [Roles.QuanTri], true);
        return new InstrumentsViewModel(store, new FakeServiceLookup(), new CurrentUser(user), serial, logs);
    }

    [Fact]
    public async Task SelectInstrument_LoadsSerialSettingsIntoForm()
    {
        var id = Guid.NewGuid();
        var store = new FakeInstrumentStore
        {
            Instruments = [new InstrumentInfo(id, "AU2700", null, "beckman-au", null, """{"type":"serial","port":"COM5","baudRate":9600,"dataBits":8,"parity":"None","stopBits":1}""", "DK10.1", true)],
        };
        var viewModel = Create(store, new FakeSerialPortService());
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.SelectedInstrument = viewModel.Instruments.Single();

        viewModel.Connection.IsSerial.ShouldBeTrue();
        viewModel.Connection.PortName.ShouldBe("COM5");
        viewModel.Connection.PortOptions.ShouldContain("COM5"); // cổng đã lưu luôn có trong ô chọn dù máy này không thấy
    }

    [Fact]
    public async Task Save_SerialWithoutPort_ShowsError()
    {
        var store = new FakeInstrumentStore();
        var viewModel = Create(store, new FakeSerialPortService());
        viewModel.EditName = "Máy thử";
        viewModel.Connection.SelectedKind = ConnectionEditorViewModel.KindOptions.First(k => k.Kind == ConnectionKind.Serial);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Chọn cổng COM");
        store.Instruments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_TcpClient_WritesJson()
    {
        var store = new FakeInstrumentStore();
        var viewModel = Create(store, new FakeSerialPortService());
        viewModel.EditName = "cobas 8000";
        viewModel.Connection.SelectedKind = ConnectionEditorViewModel.KindOptions.First(k => k.Kind == ConnectionKind.TcpClient);
        viewModel.Connection.Host = "10.0.0.8";
        viewModel.Connection.TcpPortText = "50001";

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBeNull();
        var saved = ConnectionSettings.Parse(store.Instruments.Single().ConnectionJson);
        saved.Kind.ShouldBe(ConnectionKind.TcpClient);
        saved.Host.ShouldBe("10.0.0.8");
        saved.TcpPort.ShouldBe(50001);
    }

    [Fact]
    public async Task Save_PortAlreadyUsedOnSameWorkstation_SavesWithWarning()
    {
        var store = new FakeInstrumentStore
        {
            Instruments = [new InstrumentInfo(Guid.NewGuid(), "Máy A", null, null, null, """{"type":"serial","port":"COM3"}""", "TRAM1", true)],
        };
        var viewModel = Create(store, new FakeSerialPortService());
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.EditName = "Máy B";
        viewModel.EditWorkstationCode = "TRAM1";
        viewModel.Connection.UsePort("COM3");

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.SuccessMessage.ShouldBe("Đã lưu máy. Lưu ý: COM3 cũng đang khai cho Máy A trên cùng máy trạm.");
    }

    [Fact]
    public async Task ScanAndListenAll_ShowsGuessAndBusyPort_ThenUsePort()
    {
        var serial = new FakeSerialPortService
        {
            Ports = { new SerialPortEntry("COM3", "USB-SERIAL CH340"), new SerialPortEntry("COM4", null), new SerialPortEntry("COM5", null) },
            Data = { ["COM3"] = AstmEnq },
            Busy = { "COM5" },
        };
        var viewModel = Create(new FakeInstrumentStore(), serial);
        viewModel.EditProtocolFamily = null;
        viewModel.Connection.BaudRate = 19200;

        viewModel.PortScanner.RefreshCommand.Execute(null);
        await viewModel.PortScanner.ListenAllCommand.ExecuteAsync(null);

        var rows = viewModel.PortScanner.Ports;
        rows.Count.ShouldBe(3);
        rows[0].ByteCount.ShouldBe(1);
        rows[0].Preview.ShouldBe("<ENQ>");
        rows[0].GuessFamily.ShouldBe("astm-e1394");
        rows[1].State.ShouldBe("Im lặng");
        rows[2].State.ShouldContain("đang bị chương trình khác mở");
        serial.ListenCalls.ShouldAllBe(c => c.BaudRate == 19200); // nghe theo baud đang chọn ở form
        viewModel.PortScanner.StatusText.ShouldBe("Có dữ liệu ở: COM3.");

        rows[0].UseCommand.Execute(null);

        viewModel.Connection.IsSerial.ShouldBeTrue();
        viewModel.Connection.PortName.ShouldBe("COM3");
        viewModel.EditProtocolFamily.ShouldBe("astm-e1394"); // máy chưa khai họ → điền họ đoán được
    }

    [Fact]
    public async Task ListenOnPortOfConfiguredInstrument_WritesRawLog()
    {
        var id = Guid.NewGuid();
        var store = new FakeInstrumentStore
        {
            Instruments = [new InstrumentInfo(id, "Cell-Dyn Ruby", null, "astm-e1394", null, """{"type":"serial","port":"COM4"}""", null, true)],
        };
        var serial = new FakeSerialPortService { Ports = { new SerialPortEntry("COM4", null) }, Data = { ["COM4"] = Encoding.ASCII.GetBytes("\u0005") } };
        var logs = new FakeRawLogStore();
        var viewModel = Create(store, serial, logs);
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.PortScanner.RefreshCommand.Execute(null);
        viewModel.PortScanner.Ports.Single().UsedBy.ShouldBe("Đã khai cho: Cell-Dyn Ruby");
        await viewModel.PortScanner.ListenAllCommand.ExecuteAsync(null);

        var entries = logs.Entries[id];
        entries.Count(e => e.Direction == RawLogDirection.In).ShouldBe(1);
        entries.Count(e => e.Direction == RawLogDirection.Event).ShouldBe(2); // bắt đầu + dừng nghe
    }
}

/// <summary>Màn "Log thô": lọc theo chiều/phiên, ẩn ACK lẻ, tìm chữ, hex.</summary>
public class RawLogViewModelTests
{
    private static readonly Guid InstrumentId = Guid.NewGuid();

    private static RawLogViewModel Create(out FakeRawLogStore logs)
    {
        logs = new FakeRawLogStore();
        var now = DateTimeOffset.Now;
        logs.Append(InstrumentId, new RawLogEntry(now, RawLogDirection.Event, "p1", [], "Mở cổng COM4"));
        logs.Append(InstrumentId, new RawLogEntry(now.AddMilliseconds(1), RawLogDirection.In, "p1", [0x05]));
        logs.Append(InstrumentId, new RawLogEntry(now.AddMilliseconds(2), RawLogDirection.Out, "p1", [0x06]));
        logs.Append(InstrumentId, new RawLogEntry(now.AddMilliseconds(3), RawLogDirection.In, "p1", Encoding.ASCII.GetBytes("\u00021O|1|260001234\r\u0003")));
        logs.Append(InstrumentId, new RawLogEntry(now.AddMilliseconds(4), RawLogDirection.In, "p2", [0x04]));

        var store = new FakeInstrumentStore { Instruments = [new InstrumentInfo(InstrumentId, "Cell-Dyn Ruby", null, "astm-e1394", null, null, null, true)] };
        return new RawLogViewModel(store, logs);
    }

    [Fact]
    public async Task Load_SelectsFirstInstrumentAndHidesLoneAcks()
    {
        var viewModel = Create(out _);

        await viewModel.LoadAsync();

        viewModel.SelectedInstrument!.Name.ShouldBe("Cell-Dyn Ruby");
        viewModel.Rows.Count.ShouldBe(4); // 5 bản ghi trừ 1 ACK lẻ
        viewModel.Sessions.ShouldBe([RawLogViewModel.AllSessions, "p1", "p2"]);
        viewModel.Summary.ShouldStartWith("4 / 5 bản ghi");

        viewModel.HideLoneAcks = false;
        viewModel.Rows.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Filters_DirectionSessionSearchAndHex()
    {
        var viewModel = Create(out _);
        await viewModel.LoadAsync();

        viewModel.SelectedDirection = "Sự kiện";
        viewModel.Rows.Single().Text.ShouldBe("Mở cổng COM4");

        viewModel.SelectedDirection = "Tất cả";
        viewModel.SelectedSession = "p2";
        viewModel.Rows.Single().Text.ShouldBe("<EOT>");

        viewModel.SelectedSession = RawLogViewModel.AllSessions;
        viewModel.SearchText = "260001234";
        viewModel.Rows.Single().Text.ShouldStartWith("<STX>1O|1|260001234<CR>");

        viewModel.ShowHex = true;
        viewModel.Rows.Single().Text.ShouldStartWith("000000  02 31 4F 7C");
    }

    [Fact]
    public async Task InstrumentWithoutLog_ShowsEmptySummary()
    {
        var viewModel = Create(out var logs);
        logs.Entries.Clear();

        await viewModel.LoadAsync();

        viewModel.Rows.ShouldBeEmpty();
        viewModel.Summary.ShouldBe("Chưa có log cho máy / ngày này.");
    }
}
