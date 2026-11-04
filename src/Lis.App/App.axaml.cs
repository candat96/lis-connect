using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lis.App.Services;
using Lis.App.ViewModels;
using Lis.App.Views;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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

    private void StartUp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var directory = LocalPaths.DataDirectory;
        var protector = SecretProtectors.ForCurrentOs(directory);
        var configStore = new LocalConfigStore(directory, protector);
        var config = configStore.Load();

        if (config is null)
        {
            ShowSetupWizard(desktop, configStore);
        }
        else
        {
            ShowLogin(desktop, config);
        }
    }

    private void ShowSetupWizard(IClassicDesktopStyleApplicationLifetime desktop, LocalConfigStore configStore)
    {
        var viewModel = new SetupWizardViewModel(new SetupService());
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

    private void ShowLogin(IClassicDesktopStyleApplicationLifetime desktop, LocalConfig config)
    {
        var services = new AppServices(config);
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

            ShowMain(desktop, services, user);
            window.Close();
        };

        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop, AppServices services, UserInfo user)
    {
        var currentUser = new CurrentUser(user);
        var viewModel = new MainViewModel(
            currentUser, services.Catalog, services.Orders, services.Facility, services.LabelPrinter, services.Settings, services.PrintService);
        var window = new MainWindow { DataContext = viewModel };

        _mainWindow = window;
        desktop.MainWindow = window;
        window.Show();
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
