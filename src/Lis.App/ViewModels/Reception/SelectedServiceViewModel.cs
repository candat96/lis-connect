using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Reception;

/// <summary>
/// Một dòng trong bảng dịch vụ của phiếu ở màn Tiếp nhận: mã, tên, loại mẫu, và barcode
/// (điền sau khi "Lưu và in tem" cấp barcode cho mẫu chứa dịch vụ này).
/// </summary>
public sealed partial class SelectedServiceViewModel(ServiceInfo service, string specimenName, Action<SelectedServiceViewModel> remove)
    : ObservableObject
{
    public SelectedServiceViewModel(ServiceInfo service, Action<SelectedServiceViewModel> remove)
        : this(service, "", remove)
    {
    }

    public ServiceInfo Service { get; } = service;

    public Guid ServiceId => Service.Id;

    public string Code => Service.Code;

    public string Name => Service.Name;

    public string Loai => Service.IsPanel ? $"Gói ({Service.ChildIds.Count} chỉ số)" : "Chỉ số";

    /// <summary>Tên loại mẫu (một hoặc nhiều, nếu gói có chỉ số con khác loại mẫu).</summary>
    public string SpecimenName { get; } = specimenName;

    /// <summary>Barcode đã cấp cho mẫu chứa dịch vụ này; null khi phiếu chưa lưu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeText), nameof(HasBarcode))]
    private string? _barcode;

    public bool HasBarcode => Barcode is not null;

    public string BarcodeText => Barcode ?? "cấp khi lưu";

    [ObservableProperty]
    private bool _canRemove = true;

    [RelayCommand]
    private void Remove() => remove(this);
}
