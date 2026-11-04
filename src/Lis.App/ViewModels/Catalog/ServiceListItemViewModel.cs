using Lis.Data.Catalog;

namespace Lis.App.ViewModels.Catalog;

/// <summary>Một dòng trong bảng dịch vụ của màn Danh mục.</summary>
public sealed class ServiceListItemViewModel(ServiceInfo service)
{
    public ServiceInfo Service { get; private set; } = service;

    public Guid Id => Service.Id;

    public string Code => Service.Code;

    public string Name => Service.Name;

    public string? GroupName => Service.GroupName;

    public string Loai => Service.IsPanel ? "Gói (panel)" : "Chỉ số";

    public bool Active => Service.Active;

    public void Update(ServiceInfo service) => Service = service;
}
