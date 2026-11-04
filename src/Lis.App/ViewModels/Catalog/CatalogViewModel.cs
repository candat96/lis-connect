using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho màn Danh mục: bảng dịch vụ có ô lọc, form sửa dịch vụ, và 3 tab cho dịch vụ
/// đang chọn — mỗi tab là một ViewModel riêng (<see cref="ChildrenEditor"/>,
/// <see cref="RangesEditor"/>, <see cref="QualitativeEditor"/>) được <see cref="CatalogViewModel"/>
/// nạp lại mỗi khi đổi <see cref="SelectedService"/>.
/// </summary>
public partial class CatalogViewModel : ObservableObject
{
    private readonly CatalogStore _store;

    public CatalogViewModel(CatalogStore store)
    {
        _store = store;
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
}
