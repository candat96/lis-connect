using CommunityToolkit.Mvvm.Input;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Reception;

/// <summary>Một dòng trong danh sách dịch vụ đã chọn ở màn Tiếp nhận.</summary>
public sealed partial class SelectedServiceViewModel(ServiceInfo service, Action<SelectedServiceViewModel> remove)
{
    public ServiceInfo Service { get; } = service;

    public Guid ServiceId => Service.Id;

    public string Code => Service.Code;

    public string Name => Service.Name;

    public string Loai => Service.IsPanel ? "Gói (panel)" : "Chỉ số";

    [RelayCommand]
    private void Remove() => remove(this);
}
