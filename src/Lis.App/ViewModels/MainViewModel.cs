using Avalonia.Threading;
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
using Lis.Data.His;
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
        ISetupService setupService, LocalConfigStore configStore, LocalConfig localConfig,
        HisProfileStore hisProfileStore, HisConnectionTester hisConnectionTester,
        ICatalogExcelImporter catalogExcelImporter, IFilePicker filePicker,
        ISerialPortService? serialPorts = null, IRawLogStore? rawLogs = null,
        Lis.App.Services.Connections.IInstrumentConnections? connections = null, string? workstationCode = null,
        Lis.Data.Ingest.IChannelMapLearning? channelLearning = null)
    {
        CurrentUser = currentUser;
        NavItems = BuildNavItems(currentUser);

        Catalog = new CatalogViewModel(catalogStore, catalogExcelImporter, filePicker, currentUser);
        Reception = new ReceptionViewModel(orders, catalogStore, facility, currentUser, labelPrinter);
        Results = new ResultsViewModel(results, currentUser, reportPrinter);
        Unmatched = new UnmatchedViewModel(unmatched, currentUser);
        rawLogs ??= new RawLogStore(RawLogStore.DefaultRoot);
        Instruments = new InstrumentsViewModel(
            instruments, catalogStore, currentUser, serialPorts, rawLogs, action => Dispatcher.UIThread.Post(action), connections, workstationCode, channelLearning);
        RawLog = new RawLogViewModel(instruments, rawLogs);
        Instruments.OpenRawLogRequested += (_, instrumentId) =>
        {
            var item = NavItems.FirstOrDefault(n => n.Label == "Log thô");
            if (item is null)
            {
                return;
            }

            _rawLogLoaded = true;
            SelectedItem = item;
            _ = RawLog.ShowInstrumentAsync(instrumentId);
        };
        Users = new UsersViewModel(users, currentUser);

        var dbConnection = currentUser.Can(Permission.QuanTri)
            ? new DbConnectionViewModel(setupService, configStore, localConfig)
            : null;
        if (dbConnection is not null)
        {
            dbConnection.ConfigSaved += (_, config) => ConfigSaved?.Invoke(this, config);
        }

        var hisConnection = currentUser.Can(Permission.QuanTri)
            ? new HisSettingsViewModel(hisProfileStore, hisConnectionTester, currentUser)
            : null;

        Settings = new SettingsViewModel(
            new FacilitySettingsViewModel(facility), new PrinterSettingsViewModel(settings, printService), dbConnection, hisConnection);

        // Đặt sau khi các ViewModel con đã có — OnSelectedItemChanged có thể tải màn đầu tiên ngay.
        SelectedItem = NavItems.FirstOrDefault();

        // Tự tải lại ngầm màn đang mở (kết quả máy gửi về — có thể từ máy trạm khác — hiện ra
        // không cần bấm). Chỉ chạy khi có Avalonia (không tạo hẹn giờ trong kiểm thử ViewModel).
        if (Avalonia.Application.Current is not null)
        {
            var timer = new DispatcherTimer { Interval = AutoRefreshInterval };
            timer.Tick += async (_, _) => await AutoRefreshAsync();
            timer.Start();
        }
    }

    /// <summary>Chu kỳ tự tải lại màn Kết quả / Chưa gắn mẫu.</summary>
    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

    private bool _autoRefreshing;

    /// <summary>Tải lại ngầm màn đang mở (Kết quả, Chưa gắn mẫu); bỏ qua nếu lần trước chưa xong.</summary>
    public async Task AutoRefreshAsync()
    {
        if (_autoRefreshing)
        {
            return;
        }

        _autoRefreshing = true;
        try
        {
            if (IsResultsSelected && _resultsLoaded)
            {
                await Results.RefreshQuietlyAsync();
            }
            else if (IsUnmatchedSelected && _unmatchedLoaded)
            {
                await Unmatched.RefreshQuietlyAsync();
            }
        }
        finally
        {
            _autoRefreshing = false;
        }
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

    /// <summary>ViewModel của màn "Log thô" — tải khi người dùng mở mục "Log thô" lần đầu.</summary>
    public RawLogViewModel RawLog { get; }

    /// <summary>ViewModel của màn "Tài khoản" — tải khi người dùng mở mục "Tài khoản" lần đầu.</summary>
    public UsersViewModel Users { get; }

    private bool _catalogLoaded;

    private bool _receptionLoaded;

    private bool _resultsLoaded;

    private bool _settingsLoaded;

    private bool _unmatchedLoaded;

    private bool _instrumentsLoaded;

    private bool _usersLoaded;

    private bool _rawLogLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsCatalogSelected))]
    [NotifyPropertyChangedFor(nameof(IsReceptionSelected))]
    [NotifyPropertyChangedFor(nameof(IsResultsSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsUnmatchedSelected))]
    [NotifyPropertyChangedFor(nameof(IsInstrumentsSelected))]
    [NotifyPropertyChangedFor(nameof(IsUsersSelected))]
    [NotifyPropertyChangedFor(nameof(IsRawLogSelected))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderSelected))]
    private NavItem? _selectedItem;

    public string PageTitle => SelectedItem?.Label ?? "";

    public bool IsCatalogSelected => SelectedItem?.Label == "Danh mục";

    public bool IsReceptionSelected => SelectedItem?.Label == "Tiếp nhận";

    public bool IsResultsSelected => SelectedItem?.Label == "Kết quả";

    public bool IsSettingsSelected => SelectedItem?.Label == "Cài đặt";

    public bool IsUnmatchedSelected => SelectedItem?.Label == UnmatchedLabel;

    /// <summary>Tên mục kết quả máy gửi mà LIS chưa gắn được vào mẫu/chỉ định (bảng unmatched_result).</summary>
    public const string UnmatchedLabel = "Chưa gắn mẫu";

    public bool IsInstrumentsSelected => SelectedItem?.Label == "Máy & map";

    public bool IsUsersSelected => SelectedItem?.Label == "Tài khoản";

    public bool IsRawLogSelected => SelectedItem?.Label == "Log thô";

    public bool IsPlaceholderSelected =>
        !IsCatalogSelected && !IsReceptionSelected && !IsResultsSelected && !IsSettingsSelected
        && !IsUnmatchedSelected && !IsInstrumentsSelected && !IsUsersSelected && !IsRawLogSelected;

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

        if (IsRawLogSelected && !_rawLogLoaded)
        {
            _rawLogLoaded = true;
            _ = RawLog.LoadAsync();
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
            // Không có mục "Chưa gắn mẫu" riêng (theo yêu cầu phòng khám): kết quả máy không ghép
            // được vẫn lưu ở unmatched_result để không mất, hiện trong Log thô của máy và mục
            // "Kênh máy gửi chưa map" ở bảng map kênh.
            new("Danh mục", Permission.QuanTri),
            new("Máy & map", Permission.QuanTri),
            new("Log thô", Permission.XemLogTho),
            new("Tài khoản", Permission.QuanTri),
            new("Cài đặt", Permission.QuanTri),
        ];

        return all.Where(item => user.Can(item.Permission)).ToList();
    }
}
