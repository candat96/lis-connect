using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lis.Core.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>Một lựa chọn "Kiểu kết nối" trong ô chọn.</summary>
public sealed record ConnectionKindOption(ConnectionKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Form kết nối của một máy (thay ô JSON thô): kiểu COM / TCP, cổng, baud, bit dữ liệu,
/// parity, stop bit, IP/cổng TCP. Đọc từ và ghi ra <see cref="ConnectionSettings"/>.
/// </summary>
public partial class ConnectionEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<ConnectionKindOption> KindOptions =
    [
        new(ConnectionKind.None, "Chưa cấu hình"),
        new(ConnectionKind.Serial, "Cổng COM (RS-232 / USB-serial)"),
        new(ConnectionKind.TcpServer, "TCP — LIS chờ máy nối vào"),
        new(ConnectionKind.TcpClient, "TCP — LIS nối tới máy"),
    ];

    private ConnectionSettings _original = ConnectionSettings.None;

    public IReadOnlyList<ConnectionKindOption> Kinds => KindOptions;

    public IReadOnlyList<int> BaudRates => ConnectionSettings.BaudRates;

    public IReadOnlyList<int> DataBitsOptions => ConnectionSettings.DataBitsOptions;

    public IReadOnlyList<string> ParityOptions => ConnectionSettings.ParityOptions;

    public IReadOnlyList<int> StopBitsOptions => ConnectionSettings.StopBitsOptions;

    /// <summary>Cổng COM để chọn: cổng thấy trên máy này + cổng đang lưu (dù máy này không có).</summary>
    public ObservableCollection<string> PortOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(IsTcp), nameof(IsTcpClient))]
    private ConnectionKindOption _selectedKind = KindOptions[0];

    [ObservableProperty]
    private string? _portName;

    [ObservableProperty]
    private int _baudRate = 9600;

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private string _parity = "None";

    [ObservableProperty]
    private int _stopBits = 1;

    [ObservableProperty]
    private string? _host;

    [ObservableProperty]
    private string? _tcpPortText;

    /// <summary>Cảnh báo khi JSON cũ trong CSDL không đọc được — lưu lại sẽ thay bằng cấu hình mới.</summary>
    [ObservableProperty]
    private string? _warning;

    public bool IsSerial => SelectedKind.Kind == ConnectionKind.Serial;

    public bool IsTcp => SelectedKind.Kind is ConnectionKind.TcpServer or ConnectionKind.TcpClient;

    public bool IsTcpClient => SelectedKind.Kind == ConnectionKind.TcpClient;

    public void Load(string? json)
    {
        var settings = ConnectionSettings.Parse(json);
        _original = settings;

        SelectedKind = KindOptions.First(o => o.Kind == settings.Kind);
        BaudRate = settings.BaudRate;
        DataBits = settings.DataBits;
        Parity = settings.Parity;
        StopBits = settings.StopBits;
        Host = settings.Host;
        TcpPortText = settings.TcpPort?.ToString();
        Warning = settings.UnreadableJson is null ? null : $"Cấu hình kết nối cũ không đọc được, lưu lại sẽ thay thế: {settings.UnreadableJson}";

        EnsurePortOption(settings.PortName);
        PortName = settings.PortName;
    }

    /// <summary>Cập nhật danh sách cổng sau khi quét, giữ cổng đang chọn.</summary>
    public void SetAvailablePorts(IEnumerable<string> ports)
    {
        var current = PortName;
        PortOptions.Clear();
        foreach (var port in ports)
        {
            PortOptions.Add(port);
        }

        EnsurePortOption(current);
        PortName = current;
    }

    /// <summary>Chọn một cổng COM (từ màn dò cổng) và chuyển kiểu kết nối sang COM.</summary>
    public void UsePort(string portName)
    {
        EnsurePortOption(portName);
        SelectedKind = KindOptions.First(o => o.Kind == ConnectionKind.Serial);
        PortName = portName;
    }

    /// <summary>Tham số cổng COM hiện tại (để nghe thử), với cổng chỉ định.</summary>
    public ConnectionSettings SerialSettingsFor(string portName) => new()
    {
        Kind = ConnectionKind.Serial,
        PortName = portName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
    };

    /// <summary>Dựng cấu hình từ form. Lỗi nhập liệu → trả null và <paramref name="error"/>.</summary>
    public ConnectionSettings? Build(out string? error)
    {
        int? tcpPort = null;
        if (IsTcp && !string.IsNullOrWhiteSpace(TcpPortText))
        {
            if (!int.TryParse(TcpPortText.Trim(), out var parsed))
            {
                error = "Cổng TCP phải là số";
                return null;
            }

            tcpPort = parsed;
        }

        var settings = (_original with { UnreadableJson = null }) with
        {
            Kind = SelectedKind.Kind,
            PortName = IsSerial ? PortName?.Trim() : null,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            Host = IsTcpClient ? Host?.Trim() : null,
            TcpPort = tcpPort,
        };

        error = settings.Validate();
        return error is null ? settings : null;
    }

    private void EnsurePortOption(string? portName)
    {
        if (!string.IsNullOrWhiteSpace(portName) && !PortOptions.Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            PortOptions.Add(portName);
        }
    }
}
