using CommunityToolkit.Mvvm.ComponentModel;
using Lis.App.Services;
using Lis.App.ViewModels.Catalog;
using Lis.App.ViewModels.Reception;
using Lis.App.ViewModels.Results;
using Lis.App.ViewModels.Settings;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Data.Results;
using Lis.Data.Settings;
using Lis.Reports.Printing;

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
    public MainViewModel(
        CurrentUser currentUser, CatalogStore catalogStore, IOrderService orders, FacilityStore facility, ILabelPrinter labelPrinter,
        ISettingStore settings, IPrintService printService, IResultService results, IReportPrinter reportPrinter)
    {
        CurrentUser = currentUser;
        NavItems = BuildNavItems(currentUser);

        Catalog = new CatalogViewModel(catalogStore);
        Reception = new ReceptionViewModel(orders, catalogStore, facility, currentUser, labelPrinter);
        Results = new ResultsViewModel(results, currentUser, reportPrinter);
        PrinterSettings = new PrinterSettingsViewModel(settings, printService);

        // Đặt sau khi các ViewModel con đã có — OnSelectedItemChanged có thể tải màn đầu tiên ngay.
        SelectedItem = NavItems.FirstOrDefault();
    }

    public CurrentUser CurrentUser { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>ViewModel của màn Danh mục — tải khi người dùng mở mục "Danh mục" lần đầu (xem <see cref="OnSelectedItemChanged"/>).</summary>
    public CatalogViewModel Catalog { get; }

    /// <summary>ViewModel của màn Tiếp nhận — tải khi người dùng mở mục "Tiếp nhận" lần đầu (xem <see cref="OnSelectedItemChanged"/>).</summary>
    public ReceptionViewModel Reception { get; }

    /// <summary>ViewModel của màn Kết quả — tải khi người dùng mở mục "Kết quả" lần đầu.</summary>
    public ResultsViewModel Results { get; }

    /// <summary>ViewModel của màn Cài đặt (mục Máy in) — tải khi người dùng mở mục "Cài đặt" lần đầu.</summary>
    public PrinterSettingsViewModel PrinterSettings { get; }

    private bool _catalogLoaded;

    private bool _receptionLoaded;

    private bool _resultsLoaded;

    private bool _settingsLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsCatalogSelected))]
    [NotifyPropertyChangedFor(nameof(IsReceptionSelected))]
    [NotifyPropertyChangedFor(nameof(IsResultsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderSelected))]
    private NavItem? _selectedItem;

    public string PageTitle => SelectedItem?.Label ?? "";

    public bool IsCatalogSelected => SelectedItem?.Label == "Danh mục";

    public bool IsReceptionSelected => SelectedItem?.Label == "Tiếp nhận";

    public bool IsResultsSelected => SelectedItem?.Label == "Kết quả";

    public bool IsSettingsSelected => SelectedItem?.Label == "Cài đặt";

    public bool IsPlaceholderSelected => !IsCatalogSelected && !IsReceptionSelected && !IsResultsSelected && !IsSettingsSelected;

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (IsCatalogSelected && !_catalogLoaded)
        {
            _catalogLoaded = true;
            _ = Catalog.LoadAsync();
        }

        if (IsReceptionSelected && !_receptionLoaded)
        {
            _receptionLoaded = true;
            _ = Reception.LoadAsync();
        }

        if (IsResultsSelected && !_resultsLoaded)
        {
            _resultsLoaded = true;
            _ = Results.LoadAsync();
        }

        if (IsSettingsSelected && !_settingsLoaded)
        {
            _settingsLoaded = true;
            _ = PrinterSettings.LoadAsync();
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
