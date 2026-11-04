using CommunityToolkit.Mvvm.ComponentModel;
using Lis.Core.Auth;
using Lis.Data.Auth;

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
    public MainViewModel(CurrentUser currentUser)
    {
        CurrentUser = currentUser;
        NavItems = BuildNavItems(currentUser);
        SelectedItem = NavItems.FirstOrDefault();
    }

    public CurrentUser CurrentUser { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    private NavItem? _selectedItem;

    public string PageTitle => SelectedItem?.Label ?? "";

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
