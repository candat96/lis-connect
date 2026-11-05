using CommunityToolkit.Mvvm.ComponentModel;
using Lis.App.Services;
using Lis.App.ViewModels.Admin;
using Lis.App.ViewModels.Catalog;
using Lis.App.ViewModels.Instruments;
using Lis.App.ViewModels.Reception;
using Lis.App.ViewModels.Results;
using Lis.App.ViewModels.Settings;
using Lis.App.ViewModels.Unmatched;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Local;
using Lis.Data.Orders;
using Lis.Data.Results;
using Lis.Data.Settings;
using Lis.Data.Setup;
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
        ISettingStore settings, IPrintService printService, IResultService results, IReportPrinter reportPrinter,
        IUnmatchedService unmatched, IInstrumentStore instruments, UserStore users,
        ISetupService setupService, LocalConfigStore configStore, LocalConfig localConfig)
    {
        CurrentUser = currentUser;
        NavItems = BuildNavItems(currentUser);

        Catalog = new CatalogViewModel(catalogStore);
        Reception = new ReceptionViewModel(orders, catalogStore, facility, currentUser, labelPrinter);
        Results = new ResultsViewModel(results, currentUser, reportPrinter);
        Unmatched = new UnmatchedViewModel(unmatched, currentUser);
        Instruments = new InstrumentsViewModel(instruments, catalogStore, currentUser);
        Users = new UsersViewModel(users);

        var dbConnection = currentUser.Can(Permission.QuanTri)
            ? new DbConnectionViewModel(setupService, configStore, localConfig)
            : null;
        if (dbConnection is not null)
        {
            dbConnection.ConfigSaved += (_, config) => ConfigSaved?.Invoke(this, config);
        }

        Settings = new SettingsViewModel(new FacilitySettingsViewModel(facility), new PrinterSettingsViewModel(settings, printService), dbConnection);

        // Đặt sau khi các ViewModel con đã có — OnSelectedItemChanged có thể tải màn đầu tiên ngay.
        SelectedItem = NavItems.FirstOrDefault();
    }

    /// <summary>Báo đã lưu cấu hình CSDL mới ở tab "Kết nối CSDL" — App cần đăng xuất và mở lại màn đăng nhập.</summary>
    public event EventHandler<LocalConfig>? ConfigSaved;

    public CurrentUser CurrentUser { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>ViewModel của màn Danh mục — tải khi người dùng mở mục "Danh mục" lần đầu (xem <see cref="OnSelectedItemChanged"/>).</summary>
    public CatalogViewModel Catalog { get; }

    /// <summary>ViewModel của màn Tiếp nhận — tải khi người dùng mở mục "Tiếp nhận" lần đầu (xem <see cref="OnSelectedItemChanged"/>).</summary>
    public ReceptionViewModel Reception { get; }

    /// <summary>ViewModel của màn Kết quả — tải khi người dùng mở mục "Kết quả" lần đầu.</summary>
    public ResultsViewModel Results { get; }

    /// <summary>ViewModel của màn Cài đặt (các tab con) — tải khi người dùng mở mục "Cài đặt" lần đầu.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>ViewModel của màn Chưa ghép — tải khi người dùng mở mục "Chưa ghép" lần đầu.</summary>
    public UnmatchedViewModel Unmatched { get; }

    /// <summary>ViewModel của màn "Máy &amp; map" — tải khi người dùng mở mục "Máy &amp; map" lần đầu.</summary>
    public InstrumentsViewModel Instruments { get; }

    /// <summary>ViewModel của màn "Tài khoản" — tải khi người dùng mở mục "Tài khoản" lần đầu.</summary>
    public UsersViewModel Users { get; }

    private bool _catalogLoaded;

    private bool _receptionLoaded;

    private bool _resultsLoaded;

    private bool _settingsLoaded;

    private bool _unmatchedLoaded;

    private bool _instrumentsLoaded;

    private bool _usersLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsCatalogSelected))]
    [NotifyPropertyChangedFor(nameof(IsReceptionSelected))]
    [NotifyPropertyChangedFor(nameof(IsResultsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsUnmatchedSelected))]
    [NotifyPropertyChangedFor(nameof(IsInstrumentsSelected))]
    [NotifyPropertyChangedFor(nameof(IsUsersSelected))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderSelected))]
    private NavItem? _selectedItem;

    public string PageTitle => SelectedItem?.Label ?? "";

    public bool IsCatalogSelected => SelectedItem?.Label == "Danh mục";

    public bool IsReceptionSelected => SelectedItem?.Label == "Tiếp nhận";

    public bool IsResultsSelected => SelectedItem?.Label == "Kết quả";

    public bool IsSettingsSelected => SelectedItem?.Label == "Cài đặt";

    public bool IsUnmatchedSelected => SelectedItem?.Label == "Chưa ghép";

    public bool IsInstrumentsSelected => SelectedItem?.Label == "Máy & map";

    public bool IsUsersSelected => SelectedItem?.Label == "Tài khoản";

    public bool IsPlaceholderSelected =>
        !IsCatalogSelected && !IsReceptionSelected && !IsResultsSelected && !IsSettingsSelected
        && !IsUnmatchedSelected && !IsInstrumentsSelected && !IsUsersSelected;

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
            _ = Settings.LoadAsync();
        }

        if (IsUnmatchedSelected && !_unmatchedLoaded)
        {
            _unmatchedLoaded = true;
            _ = Unmatched.LoadAsync();
        }

        if (IsInstrumentsSelected && !_instrumentsLoaded)
        {
            _instrumentsLoaded = true;
            _ = Instruments.LoadAsync();
        }

        if (IsUsersSelected && !_usersLoaded)
        {
            _usersLoaded = true;
            _ = Users.LoadUsersCommand.ExecuteAsync(null);
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
