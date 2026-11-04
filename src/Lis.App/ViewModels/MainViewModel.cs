using CommunityToolkit.Mvvm.ComponentModel;
using Lis.App.ViewModels.Catalog;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;

namespace Lis.App.ViewModels;

/// <summary>Một mục trên thanh điều hướng trái của <c>MainWindow</c>.</summary>
public sealed class NavItem(string label, Permission permission)
{
    public string Label { get; } = label;

    public Permission Permission { get; } = permission;
}

/// <summary>
/// ViewModel cho <c>MainWindow</c>: thanh điều hướng trái, hiện/ẩn mục theo quyền của
/// <see cref="CurrentUser"/>. Các màn nghiệp vụ chưa làm thì hiện trang "Đang xây dựng".
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel(CurrentUser currentUser, CatalogStore catalogStore)
    {
        CurrentUser = currentUser;
        NavItems = BuildNavItems(currentUser);
        SelectedItem = NavItems.FirstOrDefault();

        Catalog = new CatalogViewModel(catalogStore);
    }

    public CurrentUser CurrentUser { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>ViewModel của màn Danh mục — tải khi người dùng mở mục "Danh mục" lần đầu (xem <see cref="OnSelectedItemChanged"/>).</summary>
    public CatalogViewModel Catalog { get; }

    private bool _catalogLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsCatalogSelected))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderSelected))]
    private NavItem? _selectedItem;

    public string PageTitle => SelectedItem?.Label ?? "";

    public bool IsCatalogSelected => SelectedItem?.Label == "Danh mục";

    public bool IsPlaceholderSelected => !IsCatalogSelected;

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (IsCatalogSelected && !_catalogLoaded)
        {
            _catalogLoaded = true;
            _ = Catalog.LoadAsync();
        }
    }

    private static IReadOnlyList<NavItem> BuildNavItems(CurrentUser user)
    {
        NavItem[] all =
        [
            new("Tiếp nhận", Permission.TiepNhan),
            new("Kết quả", Permission.XemKetQua),
            new("Chưa ghép", Permission.GhepTay),
            new("Danh mục", Permission.QuanTri),
            new("Máy & map", Permission.QuanTri),
            new("Tài khoản", Permission.QuanTri),
            new("Cài đặt", Permission.QuanTri),
        ];

        return all.Where(item => user.Can(item.Permission)).ToList();
    }
}
