using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho màn Danh mục: bảng dịch vụ có ô lọc, form sửa dịch vụ, và 3 tab cho dịch vụ
/// đang chọn — mỗi tab là một ViewModel riêng (<see cref="ChildrenEditor"/>,
/// <see cref="RangesEditor"/>, <see cref="QualitativeEditor"/>) được <see cref="CatalogViewModel"/>
/// nạp lại mỗi khi đổi <see cref="SelectedService"/>. Nút "Nhập Excel…" nhập danh mục từ workbook
/// (xem <see cref="ImportExcelAsync"/>).
/// </summary>
public partial class CatalogViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private readonly ICatalogExcelImporter _importer;
    private readonly IFilePicker _filePicker;
    private readonly CurrentUser _currentUser;

    /// <summary>Nội dung tệp Excel đang chờ nhập — giữ trong bộ nhớ từ lúc chạy dryRun tới lúc bấm "Nhập thật", không đọc lại tệp từ đĩa.</summary>
    private byte[]? _pendingImportBytes;

    public CatalogViewModel(CatalogStore store, ICatalogExcelImporter importer, IFilePicker filePicker, CurrentUser currentUser)
    {
        _store = store;
        _importer = importer;
        _filePicker = filePicker;
        _currentUser = currentUser;
        ChildrenEditor = new ChildrenEditorViewModel(store);
        RangesEditor = new RangesEditorViewModel(store);
        QualitativeEditor = new QualitativeRulesEditorViewModel(store);
    }

    public ChildrenEditorViewModel ChildrenEditor { get; }

    public RangesEditorViewModel RangesEditor { get; }

    public QualitativeRulesEditorViewModel QualitativeEditor { get; }

    public ObservableCollection<ServiceListItemViewModel> Services { get; } = [];

    public ObservableCollection<ServiceListItemViewModel> FilteredServices { get; } = [];

    public ObservableCollection<SpecimenTypeItem> SpecimenTypes { get; } = [];

    public IReadOnlyList<ValueTypeOption> ValueTypeOptions { get; } = ValueTypeOption.All;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private ServiceListItemViewModel? _selectedService;

    [ObservableProperty]
    private string? _errorMessage;

    // Trường sửa dịch vụ (dịch vụ mới khi SelectedService null).
    [ObservableProperty]
    private Guid _editId;

    [ObservableProperty]
    private string _editCode = "";

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string? _editGroupName;

    [ObservableProperty]
    private SpecimenTypeItem? _editSpecimenType;

    [ObservableProperty]
    private string? _editUnit;

    [ObservableProperty]
    private ValueTypeOption? _editValueType = ValueTypeOption.So;

    [ObservableProperty]
    private int? _editDecimals;

    [ObservableProperty]
    private int _editPrintOrder;

    [ObservableProperty]
    private bool _editIsPanel;

    [ObservableProperty]
    private bool _editAutoApprove;

    [ObservableProperty]
    private bool _editActive = true;

    [ObservableProperty]
    private string? _editHisCode;

    /// <summary>Nạp toàn bộ dịch vụ, loại mẫu bệnh phẩm và danh sách máy từ CSDL.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var services = await _store.ListServicesAsync(activeOnly: false);
            var specimenTypes = await _store.SpecimenTypesAsync();
            var instruments = await _store.InstrumentsAsync();

            Services.Clear();
            foreach (var service in services)
            {
                Services.Add(new ServiceListItemViewModel(service));
            }

            SpecimenTypes.Clear();
            foreach (var specimenType in specimenTypes)
            {
                SpecimenTypes.Add(new SpecimenTypeItem(specimenType.Id, specimenType.Code, specimenType.Name));
            }

            var instrumentItems = instruments.Select(i => new InstrumentItem(i.Id, i.Name)).ToList();
            RangesEditor.SetInstruments(instrumentItems);
            QualitativeEditor.SetInstruments(instrumentItems);

            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được danh mục dịch vụ: {ex.Message}";
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredServices.Clear();
        var text = FilterText.Trim();
        var matches = text.Length == 0
            ? Services
            : Services.Where(s =>
                s.Code.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

        foreach (var service in matches)
        {
            FilteredServices.Add(service);
        }
    }

    partial void OnSelectedServiceChanged(ServiceListItemViewModel? value)
    {
        if (value is null)
        {
            ClearEditFields();
            ChildrenEditor.Clear();
            RangesEditor.Clear();
            QualitativeEditor.Clear();
            return;
        }

        LoadIntoEditFields(value.Service);
        ChildrenEditor.LoadForService(value.Service, Services);

        // Khoảng tham chiếu/quy đổi định tính không tự tải khi đổi SelectedService — màn hình
        // (CatalogView, qua ServiceList.SelectionChanged) gọi LoadSelectedDetailCommand. Tách
        // ra để công cụ chụp ảnh (tools/Lis.Screenshots) gán SelectedService bằng dữ liệu giả
        // mà không kéo theo một lệnh gọi CSDL thật.
        RangesEditor.Clear();
        QualitativeEditor.Clear();
    }

    private void LoadIntoEditFields(ServiceInfo s)
    {
        EditId = s.Id;
        EditCode = s.Code;
        EditName = s.Name;
        EditGroupName = s.GroupName;
        EditSpecimenType = SpecimenTypes.FirstOrDefault(t => t.Id == s.SpecimenTypeId);
        EditUnit = s.Unit;
        EditValueType = ValueTypeOptions.FirstOrDefault(v => v.Code == s.ValueType) ?? ValueTypeOption.So;
        EditDecimals = s.Decimals;
        EditPrintOrder = s.PrintOrder;
        EditIsPanel = s.IsPanel;
        EditAutoApprove = s.AutoApprove;
        EditActive = s.Active;
        EditHisCode = s.HisCode;
    }

    private void ClearEditFields()
    {
        EditId = Guid.Empty;
        EditCode = "";
        EditName = "";
        EditGroupName = null;
        EditSpecimenType = null;
        EditUnit = null;
        EditValueType = ValueTypeOption.So;
        EditDecimals = null;
        EditPrintOrder = 0;
        EditIsPanel = false;
        EditAutoApprove = false;
        EditActive = true;
        EditHisCode = null;
    }

    /// <summary>
    /// Tải khoảng tham chiếu và quy đổi định tính của <see cref="SelectedService"/>. Gọi bởi
    /// <c>CatalogView</c> khi người dùng đổi dòng chọn trên bảng dịch vụ — không tự gọi khi
    /// <see cref="SelectedService"/> đổi để công cụ chụp ảnh gán được dữ liệu giả.
    /// </summary>
    [RelayCommand]
    private async Task LoadSelectedDetailAsync()
    {
        if (SelectedService is null)
        {
            return;
        }

        var serviceId = SelectedService.Id;
        await RangesEditor.LoadAsync(serviceId);
        await QualitativeEditor.LoadAsync(serviceId);
    }

    /// <summary>Bỏ chọn dịch vụ hiện tại để chuẩn bị thêm dịch vụ mới.</summary>
    [RelayCommand]
    private void NewService() => SelectedService = null;

    [RelayCommand]
    private async Task SaveServiceAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(EditCode) || string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "Nhập mã và tên dịch vụ";
            return;
        }

        if (EditSpecimenType is null)
        {
            ErrorMessage = "Chọn loại mẫu bệnh phẩm";
            return;
        }

        if (EditValueType is null)
        {
            ErrorMessage = "Chọn kiểu giá trị";
            return;
        }

        try
        {
            var existingChildIds = SelectedService?.Service.ChildIds ?? [];
            var info = new ServiceInfo(
                EditId,
                EditCode.Trim(),
                EditName.Trim(),
                string.IsNullOrWhiteSpace(EditGroupName) ? null : EditGroupName,
                EditSpecimenType.Id,
                string.IsNullOrWhiteSpace(EditUnit) ? null : EditUnit,
                EditValueType.Code,
                EditDecimals,
                EditPrintOrder,
                EditIsPanel,
                EditAutoApprove,
                EditActive,
                string.IsNullOrWhiteSpace(EditHisCode) ? null : EditHisCode,
                existingChildIds);

            var savedId = await _store.SaveServiceAsync(info);
            await LoadAsync();
            SelectedService = Services.FirstOrDefault(s => s.Id == savedId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không lưu được dịch vụ: {ex.Message}";
        }
    }

    /// <summary>Chỉ quyền <see cref="Permission.QuanTri"/> mới nhập Excel được — nút ẩn/mờ với các vai trò khác.</summary>
    public bool CanImportExcel => _currentUser.Can(Permission.QuanTri);

    /// <summary>Panel kết quả nhập Excel (dryRun hoặc nhập thật) có đang hiện không.</summary>
    [ObservableProperty]
    private bool _isImportResultVisible;

    /// <summary>"Sẽ tạo X, cập nhật Y" (dryRun) hoặc "Đã tạo X, cập nhật Y" (nhập thật) — null khi có lỗi.</summary>
    [ObservableProperty]
    private string? _importResultMessage;

    /// <summary>Tệp đang nhập có lỗi không — chặn nút "Nhập thật" khi có.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmImport))]
    private bool _importHasErrors;

    /// <summary>Đã bấm "Nhập thật" và nhập xong (thành công hoặc lỗi) — ẩn nút "Nhập thật", chỉ còn "Đóng".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmImport))]
    [NotifyPropertyChangedFor(nameof(CancelButtonLabel))]
    private bool _importCompleted;

    /// <summary>Danh sách lỗi theo dòng (định dạng "Sheet X dòng Y: ...") của lần kiểm tra/nhập gần nhất.</summary>
    public ObservableCollection<string> ImportErrors { get; } = [];

    /// <summary>Còn tệp đang chờ, chưa có lỗi, và chưa nhập thật — nút "Nhập thật" chỉ bấm được khi true.</summary>
    public bool CanConfirmImport => !ImportHasErrors && !ImportCompleted && _pendingImportBytes is not null;

    /// <summary>Nhãn nút đóng panel: "Hủy" trước khi nhập thật, "Đóng" sau khi đã nhập xong.</summary>
    public string CancelButtonLabel => ImportCompleted ? "Đóng" : "Hủy";

    /// <summary>
    /// Mở dialog chọn tệp Excel danh mục để nhập (chỉ admin): chạy dryRun trước, hiện panel kết
    /// quả ("Sẽ tạo X, cập nhật Y" hoặc danh sách lỗi theo dòng). Người dùng bấm "Nhập thật" để
    /// ghi thật (<see cref="ConfirmImportAsync"/>) hoặc "Hủy" để đóng (<see cref="CancelImport"/>).
    /// </summary>
    [RelayCommand]
    public async Task ImportExcelAsync()
    {
        if (!CanImportExcel)
        {
            return;
        }

        ErrorMessage = null;

        var picked = await _filePicker.PickExcelFileAsync();
        if (picked is null)
        {
            // Người dùng bấm Hủy trên hộp thoại chọn tệp — không làm gì cả.
            return;
        }

        _pendingImportBytes = picked.Content;
        ImportCompleted = false;

        await RunImportAsync(dryRun: true);
    }

    /// <summary>Nút "Nhập thật": chạy lại <see cref="_importer"/> với cùng nội dung tệp đã chọn, dryRun: false, rồi nạp lại danh sách dịch vụ.</summary>
    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        if (!CanConfirmImport)
        {
            return;
        }

        await RunImportAsync(dryRun: false);

        if (!ImportHasErrors)
        {
            ImportCompleted = true;
            await LoadAsync();
        }
    }

    /// <summary>Nút "Hủy" (hoặc "Đóng" sau khi đã nhập thật): đóng panel kết quả, bỏ tệp đang chờ.</summary>
    [RelayCommand]
    private void CancelImport()
    {
        IsImportResultVisible = false;
        ImportResultMessage = null;
        ImportErrors.Clear();
        ImportHasErrors = false;
        ImportCompleted = false;
        _pendingImportBytes = null;
    }

    private async Task RunImportAsync(bool dryRun)
    {
        if (_pendingImportBytes is null)
        {
            return;
        }

        using var stream = new MemoryStream(_pendingImportBytes);
        var report = await _importer.ImportAsync(stream, dryRun);

        ImportErrors.Clear();
        foreach (var error in report.Errors)
        {
            ImportErrors.Add(error);
        }

        ImportHasErrors = report.Errors.Count > 0;
        ImportResultMessage = ImportHasErrors
            ? null
            : dryRun
                ? $"Sẽ tạo {report.Created}, cập nhật {report.Updated}."
                : $"Đã tạo {report.Created}, cập nhật {report.Updated}.";

        IsImportResultVisible = true;
    }
}
