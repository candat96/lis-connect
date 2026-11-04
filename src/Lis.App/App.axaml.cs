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
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
            configStore.Save(config);
            window.Close();
            ShowLogin(desktop, config);
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
            await services.TouchWorkstationAsync();
            window.Close();
            ShowMain(desktop, user);
        };

        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop, UserInfo user)
    {
        var currentUser = new CurrentUser(user);
        var viewModel = new MainViewModel(currentUser);
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
