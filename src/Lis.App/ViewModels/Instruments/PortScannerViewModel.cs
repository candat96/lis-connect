using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Core.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>
/// Dò cổng COM trên máy trạm: liệt kê cổng (kèm tên thiết bị và máy nào đã khai cổng đó), nghe
/// thử một hoặc tất cả cổng cùng lúc để biết cổng nào đang nhận dữ liệu từ máy nào.
/// </summary>
/// <remarks>
/// Cách dùng khi cài lên máy đã cắm sẵn cáp: bấm "Nghe tất cả cổng", rồi trên từng máy xét
/// nghiệm cho gửi lại một kết quả (hoặc bật/tắt kết nối LIS) — cổng nào hiện byte là cổng của
/// máy đó; phần "Đoán" gợi ý họ giao thức. Chỉ đọc, không gửi gì ra cổng.
/// </remarks>
public partial class PortScannerViewModel : ObservableObject
{
    private readonly ISerialPortService _serial;
    private readonly ConnectionEditorViewModel _connection;
    private readonly Func<string, string?> _usedBy;
    private readonly Action<Action> _post;
    private readonly Action<string, RawLogEntry>? _log;
    private CancellationTokenSource? _listenCts;

    /// <param name="usedBy">Tên các máy đã khai cổng này (null nếu chưa máy nào).</param>
    /// <param name="post">Đưa cập nhật về luồng giao diện (app: <c>Dispatcher.UIThread.Post</c>; kiểm thử: chạy ngay).</param>
    /// <param name="log">Ghi log thô theo cổng (gọi trên luồng nền) — bên gọi chuyển vào log của máy đã khai cổng đó.</param>
    public PortScannerViewModel(
        ISerialPortService serial, ConnectionEditorViewModel connection, Func<string, string?> usedBy,
        Action<Action>? post = null, Action<string, RawLogEntry>? log = null)
    {
        _serial = serial;
        _connection = connection;
        _usedBy = usedBy;
        _post = post ?? (action => action());
        _log = log;
    }

    public ObservableCollection<PortScanRowViewModel> Ports { get; } = [];

    public IReadOnlyList<int> DurationOptions { get; } = [10, 30, 60, 120];

    [ObservableProperty]
    private int _listenSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isListening;

    [ObservableProperty]
    private string? _statusText;

    public bool IsIdle => !IsListening;

    /// <summary>Quét lại danh sách cổng COM trên máy này.</summary>
    [RelayCommand]
    public void Refresh()
    {
        try
        {
            var ports = _serial.ListPorts();
            Ports.Clear();
            foreach (var port in ports)
            {
                Ports.Add(new PortScanRowViewModel(this, port.PortName, port.Description, _usedBy(port.PortName)));
            }

            _connection.SetAvailablePorts(ports.Select(p => p.PortName));
            StatusText = ports.Count == 0
                ? "Không thấy cổng COM nào. Kiểm tra cáp / driver USB-serial (CH340, PL2303, FTDI…) trong Device Manager."
                : $"Thấy {ports.Count} cổng COM.";
        }
        catch (Exception ex)
        {
            StatusText = $"Không liệt kê được cổng COM: {ex.Message}";
        }
    }

    /// <summary>Nghe tất cả cổng cùng lúc với tham số baud/bit/parity đang chọn ở form kết nối.</summary>
    [RelayCommand]
    private Task ListenAllAsync() => ListenAsync(Ports.ToList());

    [RelayCommand]
    private void Stop() => _listenCts?.Cancel();

    internal Task ListenOneAsync(PortScanRowViewModel row) => ListenAsync([row]);

    internal void Use(PortScanRowViewModel row)
    {
        _connection.UsePort(row.PortName);
        if (row.GuessFamily is not null)
        {
            SuggestedFamily = row.GuessFamily;
        }

        StatusText = $"Đã chọn {row.PortName} cho máy này — bấm \"Lưu máy\" để lưu.";
    }

    /// <summary>Họ giao thức đoán được của cổng vừa chọn — <see cref="InstrumentsViewModel"/> điền vào nếu máy chưa khai.</summary>
    [ObservableProperty]
    private string? _suggestedFamily;

    private async Task ListenAsync(List<PortScanRowViewModel> rows)
    {
        if (IsListening || rows.Count == 0)
        {
            return;
        }

        IsListening = true;
        _listenCts = new CancellationTokenSource();
        var settings = _connection.SerialSettingsFor("COM?");
        StatusText = $"Đang nghe {rows.Count} cổng ở {settings.Describe().Replace("COM? · ", "")} trong {ListenSeconds} giây — cho máy xét nghiệm gửi lại kết quả ngay bây giờ…";

        try
        {
            var duration = TimeSpan.FromSeconds(ListenSeconds);
            await Task.WhenAll(rows.Select(row => ListenRowAsync(row, duration, _listenCts.Token)));

            var active = rows.Where(r => r.ByteCount > 0).Select(r => r.PortName).ToList();
            StatusText = active.Count == 0
                ? "Không cổng nào nhận được dữ liệu. Kiểm tra máy đã bật gửi LIS chưa, cáp, hoặc thử baud khác."
                : $"Có dữ liệu ở: {string.Join(", ", active)}.";
        }
        finally
        {
            IsListening = false;
            _listenCts.Dispose();
            _listenCts = null;
        }
    }

    private async Task ListenRowAsync(PortScanRowViewModel row, TimeSpan duration, CancellationToken ct)
    {
        row.BeginListening();
        var settings = _connection.SerialSettingsFor(row.PortName);
        var session = $"nghe-thu-{DateTime.Now:HHmmss}";
        Log(row.PortName, session, $"Nghe thử {settings.Describe()} trong {duration.TotalSeconds:0} giây (chỉ đọc)");
        try
        {
            var bytes = await _serial.ListenAsync(settings, duration, chunk =>
            {
                SafeLog(row.PortName, new RawLogEntry(DateTimeOffset.Now, RawLogDirection.In, session, chunk));
                _post(() => row.Append(chunk));
            }, ct);
            Log(row.PortName, session, $"Dừng nghe — nhận {bytes.Length} byte");
            _post(() => row.Finish(bytes));
        }
        catch (SerialPortBusyException ex)
        {
            Log(row.PortName, session, $"Lỗi: {ex.Message}");
            _post(() => row.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            Log(row.PortName, session, $"Lỗi mở cổng: {ex.Message}");
            _post(() => row.Fail($"Không mở được cổng: {ex.Message}"));
        }
    }

    private void Log(string portName, string session, string note) =>
        SafeLog(portName, new RawLogEntry(DateTimeOffset.Now, RawLogDirection.Event, session, [], note));

    private void SafeLog(string portName, RawLogEntry entry)
    {
        try
        {
            _log?.Invoke(portName, entry);
        }
        catch (Exception)
        {
            // Ghi log thô hỏng (đĩa đầy, không quyền) không được làm hỏng việc dò cổng.
        }
    }
}

/// <summary>Một dòng trong bảng dò cổng.</summary>
public partial class PortScanRowViewModel : ObservableObject
{
    private const int PreviewLimit = 4000;

    private readonly PortScannerViewModel _owner;
    private readonly List<byte> _bytes = [];
    private bool _finished;

    public PortScanRowViewModel(PortScannerViewModel owner, string portName, string? description, string? usedBy)
    {
        _owner = owner;
        PortName = portName;
        Description = description ?? "";
        UsedBy = usedBy is null ? "" : $"Đã khai cho: {usedBy}";
        ListenCommand = new AsyncRelayCommand(() => _owner.ListenOneAsync(this));
        UseCommand = new RelayCommand(() => _owner.Use(this));
    }

    public string PortName { get; }

    public string Description { get; }

    public string UsedBy { get; }

    public IAsyncRelayCommand ListenCommand { get; }

    public IRelayCommand UseCommand { get; }

    [ObservableProperty]
    private string _state = "Chưa nghe";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private int _byteCount;

    [ObservableProperty]
    private string _preview = "";

    [ObservableProperty]
    private string _hexPreview = "";

    [ObservableProperty]
    private string? _guessText;

    [ObservableProperty]
    private string? _guessFamily;

    [ObservableProperty]
    private bool _showHex;

    public bool HasData => ByteCount > 0;

    internal void BeginListening()
    {
        _finished = false;
        _bytes.Clear();
        ByteCount = 0;
        Preview = "";
        HexPreview = "";
        GuessText = null;
        GuessFamily = null;
        State = "Đang nghe…";
    }

    internal void Append(byte[] chunk)
    {
        if (_finished)
        {
            return;
        }

        _bytes.AddRange(chunk);
        Refresh(_bytes.ToArray());
        State = "Đang nhận dữ liệu…";
    }

    internal void Finish(byte[] bytes)
    {
        _finished = true;
        _bytes.Clear();
        _bytes.AddRange(bytes);
        Refresh(bytes);
        State = bytes.Length == 0 ? "Im lặng" : $"Nhận {bytes.Length} byte";
    }

    internal void Fail(string message)
    {
        _finished = true;
        State = message;
    }

    private void Refresh(byte[] bytes)
    {
        ByteCount = bytes.Length;
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, PreviewLimit));
        Preview = RawBytes.ToReadable(head);
        HexPreview = RawBytes.ToHex(head);
        var guess = ProtocolSniffer.Guess(bytes);
        GuessFamily = guess.Family;
        GuessText = bytes.Length == 0 ? null : (guess.Family is null ? guess.Explanation : $"{guess.Family}: {guess.Explanation}");
    }
}
