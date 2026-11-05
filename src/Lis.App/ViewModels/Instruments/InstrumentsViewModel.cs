using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>
/// ViewModel cho màn "Máy &amp; map": bên trái danh sách máy, giữa form máy (tên, model, họ giao
/// thức, cấu hình driver / kết nối dạng JSON thô — đợt 2 thay bằng form theo họ giao thức, mã
/// máy trạm, hoạt động), bên phải bảng map kênh (<see cref="ChannelEditor"/>). Chỉ quyền
/// <see cref="Permission.QuanTri"/> mới sửa được.
/// </summary>
public partial class InstrumentsViewModel : ObservableObject
{
    /// <summary>Họ giao thức đợt 1 chỉ là ô chọn giá trị tự do; đợt 2 gắn driver thật.</summary>
    public static readonly IReadOnlyList<string> ProtocolFamilies = ["astm", "hl7", "csv", "khac"];

    private readonly IInstrumentStore _store;
    private readonly CurrentUser _currentUser;

    public InstrumentsViewModel(IInstrumentStore store, IServiceLookup services, CurrentUser currentUser)
    {
        _store = store;
        _currentUser = currentUser;
        ChannelEditor = new ChannelMapEditorViewModel(store, services);
    }

    public ChannelMapEditorViewModel ChannelEditor { get; }

    public ObservableCollection<InstrumentInfo> Instruments { get; } = [];

    public IReadOnlyList<string> ProtocolFamilyOptions => ProtocolFamilies;

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
    private string? _editProtocolFamily = "astm";

    [ObservableProperty]
    private string? _editDriverConfig;

    [ObservableProperty]
    private string? _editConnection;

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

        try
        {
            var savedId = await _store.SaveAsync(new InstrumentInfo(
                EditId, EditName.Trim(), EditModel, EditProtocolFamily, EditDriverConfig, EditConnection, EditWorkstationCode, EditActive));

            await LoadAsync();
            SelectedInstrument = Instruments.FirstOrDefault(i => i.Id == savedId);
            SuccessMessage = "Đã lưu máy";
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
        EditProtocolFamily = instrument.ProtocolFamily;
        EditDriverConfig = instrument.DriverConfigJson;
        EditConnection = instrument.ConnectionJson;
        EditWorkstationCode = instrument.WorkstationCode;
        EditActive = instrument.Active;
    }

    private void ClearEditFields()
    {
        EditId = Guid.Empty;
        EditName = "";
        EditModel = null;
        EditProtocolFamily = "astm";
        EditDriverConfig = null;
        EditConnection = null;
        EditWorkstationCode = null;
        EditActive = true;
    }
}
