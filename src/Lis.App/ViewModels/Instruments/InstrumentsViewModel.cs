using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Core.Auth;
using Lis.Core.Instruments;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>
/// ViewModel cho màn "Máy &amp; map": bên trái danh sách máy, giữa form máy (tên, model, họ giao
/// thức, kết nối — <see cref="Connection"/>, dò cổng COM — <see cref="PortScanner"/>, cấu hình
/// driver dạng JSON thô, mã máy trạm, hoạt động), bên phải bảng map kênh
/// (<see cref="ChannelEditor"/>). Chỉ quyền <see cref="Permission.QuanTri"/> mới sửa được.
/// </summary>
public partial class InstrumentsViewModel : ObservableObject
{
    /// <summary>Họ giao thức theo <c>docs/drivers/README.md</c> mục 2 (driver thật gắn ở Đợt 2).</summary>
    public static readonly IReadOnlyList<string> ProtocolFamilies =
    [
        "astm-e1394", "hl7-mllp", "beckman-au", "sysmex-ca", "tosoh-g8", "dirui-h", "mindray-bc", "fixed-offset", "van-ban-ban-in", "khac",
    ];

    private readonly IInstrumentStore _store;
    private readonly CurrentUser _currentUser;

    private readonly IRawLogStore? _rawLogs;

    public InstrumentsViewModel(
        IInstrumentStore store, IServiceLookup services, CurrentUser currentUser,
        ISerialPortService? serialPorts = null, IRawLogStore? rawLogs = null, Action<Action>? post = null)
    {
        _store = store;
        _currentUser = currentUser;
        _rawLogs = rawLogs;
        ChannelEditor = new ChannelMapEditorViewModel(store, services);
        Connection = new ConnectionEditorViewModel();
        PortScanner = new PortScannerViewModel(serialPorts ?? new NoSerialPorts(), Connection, InstrumentsUsingPort, post, LogForPort);
        PortScanner.PropertyChanged += (_, e) =>
        {
            // Chọn cổng từ màn dò mà máy chưa khai họ giao thức → điền họ đoán được.
            if (e.PropertyName == nameof(PortScannerViewModel.SuggestedFamily) && PortScanner.SuggestedFamily is { } family
                && string.IsNullOrWhiteSpace(EditProtocolFamily))
            {
                EditProtocolFamily = family;
            }
        };
    }

    public ChannelMapEditorViewModel ChannelEditor { get; }

    /// <summary>Form kết nối (COM / TCP) của máy đang sửa.</summary>
    public ConnectionEditorViewModel Connection { get; }

    /// <summary>Dò và nghe thử cổng COM trên máy trạm này.</summary>
    public PortScannerViewModel PortScanner { get; }

    /// <summary>Người dùng bấm "Xem log thô" — <see cref="MainViewModel"/> chuyển sang màn Log thô của máy này.</summary>
    public event EventHandler<Guid>? OpenRawLogRequested;

    public ObservableCollection<InstrumentInfo> Instruments { get; } = [];

    /// <summary>Họ giao thức để chọn — danh sách chuẩn, cộng giá trị cũ đang lưu (vd "astm") để ô chọn không hiện trống.</summary>
    public ObservableCollection<string> ProtocolFamilyOptions { get; } = new(ProtocolFamilies);

    public bool CanEdit => _currentUser.Can(Permission.QuanTri);

    [ObservableProperty]
    private InstrumentInfo? _selectedInstrument;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    // Trường sửa máy (máy mới khi SelectedInstrument null).
    [ObservableProperty]
    private Guid _editId;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string? _editModel;

    [ObservableProperty]
    private string? _editProtocolFamily = "astm-e1394";

    [ObservableProperty]
    private string? _editDriverConfig;

    [ObservableProperty]
    private string? _editWorkstationCode;

    [ObservableProperty]
    private bool _editActive = true;

    /// <summary>Nạp danh sách máy — gọi một lần khi mở màn lần đầu.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            var selectedId = SelectedInstrument?.Id;
            var instruments = await _store.ListAsync();

            Instruments.Clear();
            foreach (var instrument in instruments)
            {
                Instruments.Add(instrument);
            }

            SelectedInstrument = Instruments.FirstOrDefault(i => i.Id == selectedId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được danh sách máy: {ex.Message}";
        }
    }

    partial void OnSelectedInstrumentChanged(InstrumentInfo? value)
    {
        SuccessMessage = null;
        if (value is null)
        {
            ClearEditFields();
            ChannelEditor.Clear();
            return;
        }

        LoadIntoEditFields(value);
        _ = ChannelEditor.LoadAsync(value.Id);
    }

    /// <summary>Bỏ chọn máy hiện tại để chuẩn bị thêm máy mới.</summary>
    [RelayCommand]
    private void NewInstrument() => SelectedInstrument = null;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        ErrorMessage = null;
        SuccessMessage = null;

        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Nhập tên máy";
            return;
        }

        var connection = Connection.Build(out var connectionError);
        if (connection is null)
        {
            ErrorMessage = connectionError;
            return;
        }

        var portConflict = PortConflict(connection);
        try
        {
            var savedId = await _store.SaveAsync(new InstrumentInfo(
                EditId, EditName.Trim(), EditModel, EditProtocolFamily, EditDriverConfig, connection.ToJson(), EditWorkstationCode, EditActive));

            await LoadAsync();
            SelectedInstrument = Instruments.FirstOrDefault(i => i.Id == savedId);
            SuccessMessage = portConflict is null ? "Đã lưu máy" : $"Đã lưu máy. Lưu ý: {portConflict}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không lưu được máy: {ex.Message}";
        }
    }

    private void LoadIntoEditFields(InstrumentInfo instrument)
    {
        EditId = instrument.Id;
        EditName = instrument.Name;
        EditModel = instrument.Model;
        if (!string.IsNullOrWhiteSpace(instrument.ProtocolFamily) && !ProtocolFamilyOptions.Contains(instrument.ProtocolFamily))
        {
            ProtocolFamilyOptions.Add(instrument.ProtocolFamily);
        }

        EditProtocolFamily = instrument.ProtocolFamily;
        EditDriverConfig = instrument.DriverConfigJson;
        Connection.Load(instrument.ConnectionJson);
        EditWorkstationCode = instrument.WorkstationCode;
        EditActive = instrument.Active;
    }

    private void ClearEditFields()
    {
        EditId = Guid.Empty;
        EditName = "";
        EditModel = null;
        EditProtocolFamily = "astm-e1394";
        EditDriverConfig = null;
        Connection.Load(null);
        EditWorkstationCode = null;
        EditActive = true;
    }
    [RelayCommand]
    private void OpenRawLog()
    {
        if (EditId != Guid.Empty)
        {
            OpenRawLogRequested?.Invoke(this, EditId);
        }
    }

    /// <summary>Các máy đã khai cổng COM này, cách nhau dấu phẩy; null nếu chưa máy nào.</summary>
    private string? InstrumentsUsingPort(string portName)
    {
        var names = Instruments.Where(i => IsOnPort(i, portName)).Select(i => i.Name).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Cảnh báo khi cổng COM đã khai cho máy khác (đang dùng) cùng máy trạm — hai máy không dùng chung một cổng được.</summary>
    private string? PortConflict(ConnectionSettings connection)
    {
        if (connection.Kind != ConnectionKind.Serial)
        {
            return null;
        }

        var others = Instruments
            .Where(i => i.Id != EditId && i.Active && IsOnPort(i, connection.PortName!)
                && string.Equals(i.WorkstationCode ?? "", EditWorkstationCode ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .ToList();
        return others.Count == 0 ? null : $"{connection.PortName} cũng đang khai cho {string.Join(", ", others)} trên cùng máy trạm.";
    }

    private static bool IsOnPort(InstrumentInfo instrument, string portName)
    {
        var settings = ConnectionSettings.Parse(instrument.ConnectionJson);
        return settings.Kind == ConnectionKind.Serial && string.Equals(settings.PortName, portName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ghi byte nghe thử vào log thô của mọi máy đã khai cổng đó (chạy trên luồng nền).</summary>
    private void LogForPort(string portName, RawLogEntry entry)
    {
        if (_rawLogs is null)
        {
            return;
        }

        foreach (var instrument in Instruments.ToList().Where(i => IsOnPort(i, portName)))
        {
            _rawLogs.Append(instrument.Id, entry);
        }
    }

    /// <summary>Dùng khi không có dịch vụ cổng COM: không có cổng nào.</summary>
    private sealed class NoSerialPorts : ISerialPortService
    {
        public IReadOnlyList<SerialPortEntry> ListPorts() => [];

        public Task<byte[]> ListenAsync(ConnectionSettings settings, TimeSpan duration, Action<byte[]>? onBytes, CancellationToken ct) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
