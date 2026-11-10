using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lis.App.Services;
using Lis.App.ViewModels;
using Lis.App.Views;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Local;
using Lis.Data.Setup;

namespace Lis.App;

/// <summary>
/// Lớp ứng dụng Avalonia cho LIS Connect. Điều hướng luồng khởi động (mục 5 bản thiết kế):
/// chưa có <c>config.json</c> → trình hướng dẫn cài đặt; có rồi → đăng nhập → màn chính.
/// Cũng là nơi xử lý icon khay (mở/thoát) vì đây là trạng thái toàn ứng dụng, không phải
/// logic riêng của một cửa sổ.
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;

    /// <summary>Kết nối máy của phiên đăng nhập hiện tại — đóng khi thoát từ khay.</summary>
    private Lis.App.Services.Connections.IInstrumentConnections? _activeConnections;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Lỗi trên luồng UI (binding, command, event handler ném ra ngoài...) mặc định làm
        // crash cả ứng dụng ngay không kịp ghi gì — ghi vào loi.log rồi đánh dấu đã xử lý để
        // ứng dụng còn chạy tiếp (người dùng thấy giao diện "đứng hình" ở chỗ gây lỗi còn hơn
        // mất hẳn ứng dụng). Hai bộ bắt lỗi còn lại (AppDomain, TaskScheduler) đăng ký ở
        // Program.Main vì Dispatcher.UIThread chỉ có sau khi Avalonia khởi tạo xong.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ErrorLog.Append("Dispatcher.UIThread.UnhandledException", e.Exception);
            e.Handled = true;

            try
            {
                SimpleMessageBoxWindow.Show(
                    "LIS Connect — Lỗi không mong muốn",
                    $"Đã xảy ra lỗi không mong muốn. Chi tiết đã ghi vào {ErrorLog.LogFile}.");
            }
            catch (Exception showError)
            {
                // Hiện hộp thoại cũng lỗi (vd UI thread đang ở trạng thái bất thường ngay sau lỗi
                // gốc) — chỉ ghi log, không thử hiện lại để tránh đệ quy.
                ErrorLog.Append("Dispatcher.UIThread.UnhandledException (hiện hộp thoại lỗi)", showError);
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Đóng cửa sổ cài đặt/đăng nhập (nút X) phải thoát hẳn ứng dụng — không có cửa sổ
            // nào khác lúc đó. MainWindow không bị ảnh hưởng: nó không bao giờ thực sự Close()
            // khi người dùng bấm X (xem MainWindow.OnClosing), chỉ ẩn xuống khay.
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            StartUp(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private LocalConfigStore? _configStore;

    private void StartUp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var directory = LocalPaths.DataDirectory;
        var protector = SecretProtectors.ForCurrentOs(directory);
        var configStore = new LocalConfigStore(directory, protector);
        _configStore = configStore;

        // Tệp cấu hình hỏng (JSON lỗi, hoặc mã hóa DPAPI của máy khác) không được crash lúc
        // khởi động — coi như chưa cài đặt, báo người dùng rồi mở lại trình hướng dẫn.
        var config = configStore.TryLoadOrRecover(out var configError);
        if (configError is not null)
        {
            SimpleMessageBoxWindow.Show("LIS Connect — Cấu hình cục bộ", configError);
        }

        if (config is null)
        {
            ShowSetupWizard(desktop, configStore);
            return;
        }

        // Migrate ngay sau khi đọc cấu hình, trước khi hiện màn đăng nhập: CSDL có thể đã được
        // nâng cấp lên bản schema mới hơn (cập nhật ứng dụng) hoặc lần migrate trước đó (ở trình
        // hướng dẫn cài đặt hoặc lần chạy trước) đã lỗi giữa chừng — migration là idempotent nên
        // gọi lại an toàn. StartupMigrationRunner bọc cả trường hợp Migrator.Upgrade ném ngoại lệ
        // (vd CSDL không kết nối được — EnsureUuidv7Compat's connection.Open() ném ra ngay) lẫn
        // trường hợp thất bại "sạch" (MigrationResult.Ok == false), để không bao giờ ném ra khỏi
        // StartUp làm crash ứng dụng không kịp hiện gì.
        var migration = StartupMigrationRunner.Run(() => Migrator.Upgrade(config.Db.ToConnectionString()));
        if (!migration.Ok)
        {
            if (migration.Exception is not null)
            {
                ErrorLog.Append("Migrator.Upgrade (startup)", migration.Exception);
            }
            else
            {
                ErrorLog.Append("Migrator.Upgrade (startup)", migration.ErrorMessage ?? "lỗi không rõ");
            }

            ShowMigrationFailureDialog(desktop, configStore, config, migration.ErrorMessage);
            return;
        }

        ShowLogin(desktop, config);
    }

    /// <summary>
    /// Hiện thông báo lỗi migration kèm hai lựa chọn: "Thử lại" (chạy lại toàn bộ luồng khởi
    /// động — CSDL/PostgreSQL có thể đã được khởi động lại trong lúc này) hoặc "Cài đặt lại kết
    /// nối" (mở trình hướng dẫn cài đặt, điền sẵn từ cấu hình hiện tại, để đổi host/port/tài
    /// khoản nếu đó là nguyên nhân). Mỗi lựa chọn mở cửa sổ tiếp theo trước khi hộp thoại này
    /// đóng (xem <see cref="SimpleMessageBoxWindow.ShowWithActions"/>).
    /// </summary>
    private void ShowMigrationFailureDialog(
        IClassicDesktopStyleApplicationLifetime desktop,
        LocalConfigStore configStore,
        LocalConfig config,
        string? errorMessage)
    {
        var detail = string.IsNullOrWhiteSpace(errorMessage) ? "" : $"\n\nChi tiết: {errorMessage}";
        SimpleMessageBoxWindow.ShowWithActions(
            "LIS Connect — Lỗi cập nhật CSDL",
            $"Không kết nối được CSDL {config.Db.Host}:{config.Db.Port}. Kiểm tra dịch vụ PostgreSQL " +
            "đang chạy, rồi mở lại phần mềm. Nếu cần đổi kết nối, chọn 'Cài đặt lại kết nối'." + detail,
            ("Thử lại", () => StartUp(desktop)),
            ("Cài đặt lại kết nối", () => ShowSetupWizard(desktop, configStore, config)));
    }

    private void ShowSetupWizard(IClassicDesktopStyleApplicationLifetime desktop, LocalConfigStore configStore, LocalConfig? prefill = null)
    {
        var viewModel = new SetupWizardViewModel(new SetupService());
        if (prefill is not null)
        {
            viewModel.Host = prefill.Db.Host;
            viewModel.Port = prefill.Db.Port;
            viewModel.Database = prefill.Db.Database;
            viewModel.Username = prefill.Db.Username;
            viewModel.Password = prefill.Db.Password;
            viewModel.RequireSsl = prefill.Db.RequireSsl;
            viewModel.WorkstationCode = prefill.WorkstationCode;
        }

        var window = new SetupWizardWindow { DataContext = viewModel };

        viewModel.Completed += (_, config) =>
        {
            // Lưu cấu hình & mở màn đăng nhập trước, đóng cửa sổ cài đặt sau — nếu đóng trước,
            // có một khoảnh khắc không còn cửa sổ nào mở, và ShutdownMode.OnLastWindowClose sẽ
            // thoát ứng dụng trước khi màn đăng nhập kịp hiện.
            configStore.Save(config);
            ShowLogin(desktop, config);
            window.Close();
        };

        desktop.MainWindow = window;
        window.Show();
    }

    private static RememberedLogin Remembered() =>
        new(LocalPaths.DataDirectory, SecretProtectors.ForCurrentOs(LocalPaths.DataDirectory));

    /// <summary>Phiên đã ghi nhớ còn hợp lệ (tài khoản còn hoạt động, chưa đổi mật khẩu) → tài khoản; không thì null.</summary>
    private static UserInfo? TryResume(AppServices services)
    {
        if (Remembered().TryLoad() is not { } saved)
        {
            return null;
        }

        try
        {
            // Task.Run: không giữ ngữ cảnh UI khi chờ đồng bộ lúc khởi động (như bước migrate).
            var user = Task.Run(async () =>
            {
                var resumed = await services.Sessions.ResumeAsync(saved.UserId, saved.Stamp);
                if (resumed is not null)
                {
                    await services.TouchWorkstationAsync();
                }

                return resumed;
            }).GetAwaiter().GetResult();
            if (user is null)
            {
                Remembered().Clear(); // đổi mật khẩu / ngừng tài khoản → phải đăng nhập lại
            }

            return user;
        }
        catch (Exception)
        {
            return null; // CSDL chưa lên — hiện màn đăng nhập như thường, giữ phiên cho lần sau.
        }
    }

    private void ShowLogin(IClassicDesktopStyleApplicationLifetime desktop, LocalConfig config)
    {
        var services = new AppServices(config);
        _ = SwitchConnectionsAsync(services.Connections);
        if (TryResume(services) is { } rememberedUser)
        {
            ShowMain(desktop, services, rememberedUser, config);
            return;
        }

        var viewModel = new LoginViewModel(services.Auth);
        var window = new LoginWindow { DataContext = viewModel };

        viewModel.LoggedIn += async (_, user) =>
        {
            try
            {
                await services.TouchWorkstationAsync();
            }
            catch (Exception ex)
            {
                // Đăng nhập đã đúng, nhưng cập nhật máy trạm thất bại (vd CSDL vừa mất kết nối) —
                // báo lỗi và ở lại màn đăng nhập thay vì mở màn chính với trạng thái nửa vời.
                viewModel.ErrorMessage = $"Đăng nhập được nhưng cập nhật máy trạm thất bại: {ex.Message}";
                return;
            }

            try
            {
                if (viewModel.RememberMe && await services.Sessions.SessionStampAsync(user.Id) is { } stamp)
                {
                    Remembered().Save(user.Id, stamp);
                }
                else
                {
                    Remembered().Clear();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Append("Ghi nhớ đăng nhập", ex); // không ghi nhớ được cũng không chặn đăng nhập
            }

            ShowMain(desktop, services, user, config);
            window.Close();
        };

        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop, AppServices services, UserInfo user, LocalConfig config)
    {
        var currentUser = new CurrentUser(user);
        var configStore = _configStore ?? new LocalConfigStore(LocalPaths.DataDirectory, SecretProtectors.ForCurrentOs(LocalPaths.DataDirectory));
        var viewModel = new MainViewModel(
            currentUser, services.Catalog, services.Orders, services.Facility, services.LabelPrinter, services.Settings, services.PrintService,
            services.Results, services.ReportPrinter, services.Unmatched, services.Instruments, services.Users,
            new SetupService(), configStore, config,
            services.HisProfiles, services.HisConnectionTester,
            services.CatalogExcelImporter, services.FilePicker,
            services.SerialPorts, services.RawLogs, services.Connections, services.WorkstationCode, services.ChannelLearning);
        var window = new MainWindow { DataContext = viewModel };


        // Lưu cấu hình CSDL mới ở tab "Kết nối CSDL" -> đăng xuất: mở màn đăng nhập với cấu
        // hình mới trước, rồi mới đóng MainWindow qua AllowClose (cùng lý do như ShowSetupWizard:
        // tránh khoảnh khắc không còn cửa sổ nào mở khiến ứng dụng tự thoát).
        viewModel.ConfigSaved += (_, newConfig) =>
        {
            ShowLogin(desktop, newConfig);
            window.AllowClose = true;
            window.Close();
        };

        // Đăng xuất: xoá phiên đã ghi nhớ rồi mở màn đăng nhập (mở trước, đóng sau — như ConfigSaved).
        viewModel.LogoutRequested += (_, _) =>
        {
            Remembered().Clear();
            ShowLogin(desktop, config);
            window.AllowClose = true;
            window.Close();
        };

        _mainWindow = window;
        desktop.MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Mở kết nối máy xét nghiệm ngay từ màn đăng nhập — máy tính khởi động lại (mất điện…) mà
    /// chưa ai đăng nhập thì kết quả máy gửi vẫn phải được nhận. Đổi cấu hình CSDL → đóng bộ cũ
    /// trước khi mở bộ mới. Lỗi (CSDL chưa lên) không chặn giao diện; mở màn Máy &amp; map / lưu máy
    /// sẽ nạp lại.
    /// </summary>
    private async Task SwitchConnectionsAsync(Lis.App.Services.Connections.IInstrumentConnections next)
    {
        var previous = _activeConnections;
        _activeConnections = next;
        try
        {
            if (previous is not null && !ReferenceEquals(previous, next))
            {
                await previous.StopAllAsync();
            }

        }
        catch (Exception)
        {
            // Đóng bộ cũ lỗi: bỏ qua, vẫn mở bộ mới.
        }

        // CSDL chưa lên (máy chủ khởi động chậm hơn máy trạm…) → thử lại mỗi 30 giây.
        while (ReferenceEquals(_activeConnections, next))
        {
            try
            {
                await next.ReloadAsync();
                return;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>Menu khay — "Mở": hiện lại cửa sổ chính nếu đang ẩn.</summary>
    private void OnTrayOpenClicked(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Menu khay — "Thoát": thoát ứng dụng thật (khác với đóng cửa sổ, chỉ ẩn xuống khay).</summary>
    private void OnTrayExitClicked(object? sender, EventArgs e)
    {
        // Đóng cổng COM/TCP gọn gàng trước khi thoát (tối đa vài giây).
        _activeConnections?.StopAllAsync().Wait(TimeSpan.FromSeconds(3));

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
        }

        if (ApplicationLifetime is IControlledApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }
}
