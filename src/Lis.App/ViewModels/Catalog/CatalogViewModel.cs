using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>
/// ViewModel cho màn Danh mục: bảng dịch vụ có ô lọc, form sửa dịch vụ, và 3 tab cho dịch vụ
/// đang chọn — chỉ số con (chỉ áp dụng cho panel), khoảng tham chiếu, quy đổi định tính.
/// </summary>
public partial class CatalogViewModel : ObservableObject
{
    private readonly CatalogStore _store;

    public CatalogViewModel(CatalogStore store)
    {
        _store = store;
    }

    public ObservableCollection<ServiceListItemViewModel> Services { get; } = [];

    public ObservableCollection<ServiceListItemViewModel> FilteredServices { get; } = [];

    public ObservableCollection<SpecimenTypeItem> SpecimenTypes { get; } = [];

    public IReadOnlyList<ValueTypeOption> ValueTypeOptions { get; } = ValueTypeOption.All;

    public ObservableCollection<ChildServiceViewModel> Children { get; } = [];

    public ObservableCollection<ReferenceRangeRowViewModel> Ranges { get; } = [];

    public ObservableCollection<QualitativeRuleRowViewModel> QualitativeRules { get; } = [];

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private ServiceListItemViewModel? _selectedService;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _childError;

    [ObservableProperty]
    private string _addChildCode = "";

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

    /// <summary>Nạp toàn bộ dịch vụ và loại mẫu bệnh phẩm từ CSDL.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var services = await _store.ListServicesAsync(activeOnly: false);
            var specimenTypes = await _store.SpecimenTypesAsync();

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
        ChildError = null;

        if (value is null)
        {
            ClearEditFields();
            Children.Clear();
            Ranges.Clear();
            QualitativeRules.Clear();
            return;
        }

        LoadIntoEditFields(value.Service);
        LoadChildrenFromService(value.Service);

        // Khoảng tham chiếu/quy đổi định tính không tự tải khi đổi SelectedService — màn hình
        // (CatalogView, qua ServiceList.SelectionChanged) gọi LoadSelectedDetailCommand. Tách
        // ra để công cụ chụp ảnh (tools/Lis.Screenshots) gán SelectedService bằng dữ liệu giả
        // mà không kéo theo một lệnh gọi CSDL thật.
        Ranges.Clear();
        QualitativeRules.Clear();
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

    private void LoadChildrenFromService(ServiceInfo panel)
    {
        Children.Clear();
        foreach (var childId in panel.ChildIds)
        {
            var child = Services.FirstOrDefault(s => s.Id == childId);
            if (child is not null)
            {
                Children.Add(NewChildRow(child.Id, child.Code, child.Name));
            }
        }
    }

    private ChildServiceViewModel NewChildRow(Guid id, string code, string name) =>
        new(id, code, name, remove: RemoveChild, moveUp: MoveChildUp, moveDown: MoveChildDown);

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
        try
        {
            var ranges = await _store.RangesAsync(serviceId);
            Ranges.Clear();
            foreach (var range in ranges)
            {
                Ranges.Add(ReferenceRangeRowViewModel.From(range, RemoveRange));
            }

            var rules = await _store.QualitativeRulesAsync(serviceId);
            QualitativeRules.Clear();
            foreach (var rule in rules)
            {
                QualitativeRules.Add(QualitativeRuleRowViewModel.From(rule, RemoveQualitativeRule));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không tải được chi tiết dịch vụ: {ex.Message}";
        }
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

    [RelayCommand]
    private async Task AddChildAsync()
    {
        ChildError = null;

        if (SelectedService is null)
        {
            ChildError = "Chọn dịch vụ cha (panel) trước";
            return;
        }

        if (string.IsNullOrWhiteSpace(AddChildCode))
        {
            return;
        }

        try
        {
            var child = await _store.FindByCodeAsync(AddChildCode.Trim());
            if (child is null)
            {
                ChildError = "Không tìm thấy mã dịch vụ này";
                return;
            }

            if (child.Id == SelectedService.Id)
            {
                ChildError = "Dịch vụ không thể là con của chính nó";
                return;
            }

            if (child.IsPanel)
            {
                ChildError = "Chỉ số con không được là một dịch vụ cha (panel)";
                return;
            }

            if (Children.Any(c => c.Id == child.Id))
            {
                ChildError = "Chỉ số này đã có trong danh sách";
                return;
            }

            Children.Add(NewChildRow(child.Id, child.Code, child.Name));
            await PersistChildrenAsync();
            AddChildCode = "";
        }
        catch (Exception ex)
        {
            ChildError = $"Không thêm được chỉ số con: {ex.Message}";
        }
    }

    private void RemoveChild(ChildServiceViewModel child)
    {
        Children.Remove(child);
        _ = PersistChildrenAsync();
    }

    private void MoveChildUp(ChildServiceViewModel child)
    {
        var index = Children.IndexOf(child);
        if (index <= 0)
        {
            return;
        }

        Children.Move(index, index - 1);
        _ = PersistChildrenAsync();
    }

    private void MoveChildDown(ChildServiceViewModel child)
    {
        var index = Children.IndexOf(child);
        if (index < 0 || index >= Children.Count - 1)
        {
            return;
        }

        Children.Move(index, index + 1);
        _ = PersistChildrenAsync();
    }

    private async Task PersistChildrenAsync()
    {
        if (SelectedService is null)
        {
            return;
        }

        try
        {
            await _store.SetChildrenAsync(SelectedService.Id, Children.Select(c => c.Id).ToList());
        }
        catch (Exception ex)
        {
            ChildError = $"Không lưu được danh sách chỉ số con: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddRange() => Ranges.Add(new ReferenceRangeRowViewModel(RemoveRange));

    private void RemoveRange(ReferenceRangeRowViewModel row) => Ranges.Remove(row);

    [RelayCommand]
    private async Task SaveRangesAsync()
    {
        if (SelectedService is null)
        {
            ErrorMessage = "Chọn dịch vụ trước";
            return;
        }

        try
        {
            var ranges = Ranges.Select(r => r.ToReferenceRange(SelectedService.Id)).ToList();
            await _store.SaveRangesAsync(SelectedService.Id, ranges);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không lưu được khoảng tham chiếu: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddQualitativeRule() => QualitativeRules.Add(new QualitativeRuleRowViewModel(RemoveQualitativeRule));

    private void RemoveQualitativeRule(QualitativeRuleRowViewModel row) => QualitativeRules.Remove(row);

    [RelayCommand]
    private async Task SaveQualitativeRulesAsync()
    {
        if (SelectedService is null)
        {
            ErrorMessage = "Chọn dịch vụ trước";
            return;
        }

        try
        {
            var rules = QualitativeRules.Select(r => r.ToRule(SelectedService.Id)).ToList();
            await _store.SaveQualitativeRulesAsync(SelectedService.Id, rules);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không lưu được quy đổi định tính: {ex.Message}";
        }
    }
}
