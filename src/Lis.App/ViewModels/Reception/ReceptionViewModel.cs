using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Core.Orders;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Reports.Labels;

namespace Lis.App.ViewModels.Reception;

/// <summary>
/// ViewModel cho màn Tiếp nhận: tìm/nhập người bệnh, chọn dịch vụ (gõ mã + Enter, gõ chữ thì
/// gợi ý), xem trước nhóm mẫu theo loại mẫu bệnh phẩm, và "Lưu và in tem" (F9) — tạo chỉ
/// định, cấp barcode (<see cref="OrderService.CreateAsync"/>), hiện bảng barcode đã cấp rồi
/// in tem qua <see cref="ILabelPrinter"/>.
/// </summary>
public partial class ReceptionViewModel : ObservableObject
{
    private readonly IOrderService _orders;
    private readonly CatalogStore _catalog;
    private readonly FacilityStore _facility;
    private readonly CurrentUser _currentUser;
    private readonly ILabelPrinter _printer;
    private readonly TimeProvider _clock;

    private List<ServiceInfo> _allServices = [];
    private Dictionary<Guid, ServiceInfo> _servicesById = [];
    private Dictionary<Guid, string> _specimenNameById = [];
    private string _barcodeMode = SampleGrouper.PerSpecimen;

    public ReceptionViewModel(
        IOrderService orders, CatalogStore catalog, FacilityStore facility, CurrentUser currentUser, ILabelPrinter printer,
        TimeProvider? clock = null)
    {
        _orders = orders;
        _catalog = catalog;
        _facility = facility;
        _currentUser = currentUser;
        _printer = printer;
        _clock = clock ?? TimeProvider.System;
    }

    public ObservableCollection<PatientHit> PatientSearchResults { get; } = [];

    public ObservableCollection<ServiceInfo> ServiceSuggestions { get; } = [];

    public ObservableCollection<SelectedServiceViewModel> SelectedServices { get; } = [];

    public ObservableCollection<SampleGroupPreviewViewModel> GroupPreview { get; } = [];

    public ObservableCollection<CreatedSample> IssuedSamples { get; } = [];

    public IReadOnlyList<SexOption> SexOptions { get; } = SexOption.All;

    [ObservableProperty]
    private string _patientSearchQuery = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExistingPatient))]
    private PatientHit? _selectedPatientHit;

    [ObservableProperty]
    private string _newPatientFullName = "";

    [ObservableProperty]
    private string _newPatientDobText = "";

    [ObservableProperty]
    private string _newPatientBirthYearText = "";

    [ObservableProperty]
    private SexOption _newPatientSex = SexOption.Male;

    [ObservableProperty]
    private string? _newPatientAddress;

    [ObservableProperty]
    private string? _newPatientPhone;

    [ObservableProperty]
    private string _serviceCodeInput = "";

    [ObservableProperty]
    private string? _orderingDoctor;

    [ObservableProperty]
    private string? _orderingDept;

    [ObservableProperty]
    private string? _diagnosis;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsExistingPatient => SelectedPatientHit is not null;

    /// <summary>
    /// Nạp danh mục dịch vụ và loại mẫu trực tiếp (không qua CSDL) — dùng cho công cụ chụp
    /// ảnh (tools/Lis.Screenshots), cùng cách <c>RangesEditorViewModel.SetInstruments</c> làm.
    /// </summary>
    public void SeedCatalog(IReadOnlyList<ServiceInfo> services, IReadOnlyDictionary<Guid, string> specimenNameById, string barcodeMode)
    {
        _allServices = services.ToList();
        _servicesById = _allServices.ToDictionary(s => s.Id);
        _specimenNameById = new Dictionary<Guid, string>(specimenNameById);
        _barcodeMode = barcodeMode;
    }

    /// <summary>Nạp danh mục dịch vụ và chế độ cấp barcode của cơ sở — gọi một lần khi màn Tiếp nhận mở lần đầu.</summary>
    public async Task LoadAsync()
    {
        try
        {
            _allServices = (await _catalog.ListServicesAsync(activeOnly: true)).ToList();
            _servicesById = _allServices.ToDictionary(s => s.Id);

            var specimenTypes = await _catalog.SpecimenTypesAsync();
            _specimenNameById = specimenTypes.ToDictionary(s => s.Id, s => s.Name);

            var facility = await _facility.GetAsync();
            if (facility is not null)
            {
                _barcodeMode = facility.BarcodeMode;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được danh mục dịch vụ: {ex.Message}";
        }
    }

    partial void OnPatientSearchQueryChanged(string value)
    {
        PatientSearchResults.Clear();
    }

    [RelayCommand]
    private async Task SearchPatientsAsync()
    {
        if (string.IsNullOrWhiteSpace(PatientSearchQuery))
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            var hits = await _orders.FindPatientsAsync(PatientSearchQuery);
            PatientSearchResults.Clear();
            foreach (var hit in hits)
            {
                PatientSearchResults.Add(hit);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Tìm người bệnh thất bại: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PickPatient(PatientHit hit)
    {
        SelectedPatientHit = hit;
        PatientSearchResults.Clear();
        PatientSearchQuery = "";
    }

    [RelayCommand]
    private void StartNewPatient()
    {
        SelectedPatientHit = null;
        NewPatientFullName = "";
        NewPatientDobText = "";
        NewPatientBirthYearText = "";
        NewPatientSex = SexOption.Male;
        NewPatientAddress = null;
        NewPatientPhone = null;
    }

    /// <summary>Gõ mã dịch vụ + Enter: thêm đúng dịch vụ đó. Gõ chữ: lọc gợi ý theo mã hoặc tên.</summary>
    [RelayCommand]
    private void AddServiceByCode()
    {
        var input = ServiceCodeInput.Trim();
        if (input.Length == 0)
        {
            return;
        }

        var exact = _allServices.FirstOrDefault(s => string.Equals(s.Code, input, StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            ErrorMessage = $"Không tìm thấy dịch vụ mã \"{input}\"";
            RefreshServiceSuggestions(input);
            return;
        }

        if (!TryAddService(exact))
        {
            return;
        }

        ServiceCodeInput = "";
        ServiceSuggestions.Clear();
        ErrorMessage = null;
    }

    partial void OnServiceCodeInputChanged(string value) => RefreshServiceSuggestions(value);

    private void RefreshServiceSuggestions(string input)
    {
        ServiceSuggestions.Clear();

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        foreach (var service in _allServices.Where(s =>
                     s.Code.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                 .Take(10))
        {
            ServiceSuggestions.Add(service);
        }
    }

    [RelayCommand]
    private void PickServiceSuggestion(ServiceInfo service)
    {
        if (!TryAddService(service))
        {
            return;
        }

        ServiceCodeInput = "";
        ServiceSuggestions.Clear();
        ErrorMessage = null;
    }

    /// <summary>
    /// Thêm dịch vụ vào danh sách đã chọn — kiểm tra trước bằng đúng quy tắc
    /// <see cref="OrderService.CreateAsync"/> sẽ áp dụng (<see cref="ServiceSelectionValidator"/>),
    /// để người dùng thấy lỗi "chọn trùng chỉ số" ngay lúc thêm chứ không phải lúc Lưu.
    /// </summary>
    private bool TryAddService(ServiceInfo service)
    {
        var wouldBeSelection = SelectedServices.Select(s => s.ServiceId).Append(service.Id);

        try
        {
            ServiceSelectionValidator.Validate(wouldBeSelection, _servicesById);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }

        SelectedServices.Add(new SelectedServiceViewModel(service, RemoveSelectedService));
        RefreshGroupPreview();
        return true;
    }

    private void RemoveSelectedService(SelectedServiceViewModel item)
    {
        SelectedServices.Remove(item);
        RefreshGroupPreview();
    }

    private void RefreshGroupPreview()
    {
        GroupPreview.Clear();

        var leaves = new List<OrderedService>();
        foreach (var selected in SelectedServices)
        {
            var service = selected.Service;
            if (service.IsPanel)
            {
                foreach (var childId in service.ChildIds)
                {
                    if (_servicesById.TryGetValue(childId, out var child))
                    {
                        leaves.Add(new OrderedService(child.Id, child.SpecimenTypeId));
                    }
                }
            }
            else
            {
                leaves.Add(new OrderedService(service.Id, service.SpecimenTypeId));
            }
        }

        foreach (var group in SampleGrouper.Group(leaves, _barcodeMode))
        {
            var names = group.ServiceIds
                .Select(id => _servicesById.TryGetValue(id, out var s) ? s.Name : "?")
                .ToList();
            var specimenName = _specimenNameById.GetValueOrDefault(group.SpecimenTypeId, "?");
            GroupPreview.Add(new SampleGroupPreviewViewModel(specimenName, names));
        }
    }

    /// <summary>
    /// "Lưu và in tem" (F9): tạo chỉ định, cấp barcode, rồi in tem qua <see cref="ILabelPrinter"/>.
    /// Chỉ định đã lưu xong trước khi in, nên in lỗi chỉ báo lỗi chứ không làm mất chỉ định —
    /// người dùng in lại tem sau.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;

        Guid? patientId = SelectedPatientHit?.Id;
        NewPatient? newPatient = null;

        if (patientId is null)
        {
            if (!TryBuildNewPatient(out newPatient, out var buildError))
            {
                ErrorMessage = buildError;
                return;
            }
        }

        if (SelectedServices.Count == 0)
        {
            ErrorMessage = "Chưa chọn dịch vụ nào";
            return;
        }

        IsBusy = true;
        try
        {
            var order = new NewOrder(
                patientId,
                newPatient,
                SelectedServices.Select(s => s.ServiceId).ToList(),
                string.IsNullOrWhiteSpace(OrderingDoctor) ? null : OrderingDoctor,
                string.IsNullOrWhiteSpace(OrderingDept) ? null : OrderingDept,
                string.IsNullOrWhiteSpace(Diagnosis) ? null : Diagnosis);

            OrderCreated created;
            try
            {
                created = await _orders.CreateAsync(order, _currentUser.User.Id);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Lưu chỉ định thất bại: {ex.Message}";
                return;
            }

            IssuedSamples.Clear();
            foreach (var sample in created.Samples)
            {
                IssuedSamples.Add(sample);
            }

            var labels = BuildLabels(SelectedPatientHit, newPatient, created.Samples);

            SelectedServices.Clear();
            GroupPreview.Clear();
            StartNewPatient();

            try
            {
                await _printer.PrintAsync(labels);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Đã lưu chỉ định nhưng in tem lỗi: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            // Lỗi ngoài hai bước trên (dựng tem, đặt lại form) — không để thoát khỏi lệnh async.
            ErrorMessage = $"Lưu chỉ định gặp lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Nội dung tem cho các mẫu vừa cấp — họ tên/năm sinh/giới tính lấy từ người bệnh đã chọn hoặc vừa nhập, ngày theo giờ máy.</summary>
    private IReadOnlyList<LabelData> BuildLabels(PatientHit? existing, NewPatient? created, IReadOnlyList<CreatedSample> samples)
    {
        var day = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

        return existing is not null
            ? LabelDataBuilder.Build(existing.FullName, existing.Dob, existing.BirthYear, existing.Sex, samples, day)
            : LabelDataBuilder.Build(created!.FullName, created.Dob, created.BirthYear, created.Sex, samples, day);
    }

    private bool TryBuildNewPatient(out NewPatient? patient, out string? error)
    {
        patient = null;

        if (string.IsNullOrWhiteSpace(NewPatientFullName))
        {
            error = "Chưa nhập họ tên người bệnh";
            return false;
        }

        DateOnly? dob = null;
        int? birthYear = null;

        if (!string.IsNullOrWhiteSpace(NewPatientDobText))
        {
            if (!DateOnly.TryParseExact(NewPatientDobText.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDob))
            {
                error = "Ngày sinh phải theo dạng dd/MM/yyyy";
                return false;
            }

            dob = parsedDob;
        }
        else if (!string.IsNullOrWhiteSpace(NewPatientBirthYearText))
        {
            if (!int.TryParse(NewPatientBirthYearText.Trim(), out var parsedYear))
            {
                error = "Năm sinh không hợp lệ";
                return false;
            }

            birthYear = parsedYear;
        }
        else
        {
            error = "Phải nhập ngày sinh hoặc ít nhất năm sinh";
            return false;
        }

        patient = new NewPatient(
            NewPatientFullName.Trim(),
            dob,
            birthYear,
            NewPatientSex.Code,
            string.IsNullOrWhiteSpace(NewPatientAddress) ? null : NewPatientAddress,
            string.IsNullOrWhiteSpace(NewPatientPhone) ? null : NewPatientPhone);

        error = null;
        return true;
    }
}
