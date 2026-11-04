using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Lis.App.ViewModels;
using Lis.App.Views;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Local;
using Lis.Data.Setup;

namespace Lis.Screenshots;

/// <summary>
/// Công cụ dòng lệnh dựng từng View của LIS Connect với một ViewModel đã nạp dữ liệu giả,
/// dùng Avalonia.Headless (+ Avalonia.Skia để có pixel thật) để chụp ảnh PNG kiểm tra giao
/// diện mà không cần máy có màn hình. Ảnh lưu ở <c>artifacts/screenshots/</c> (đã gitignore).
/// </summary>
internal static class Program
{
    public static void Main()
    {
        var outputDirectory = Path.Combine(FindRepoRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(outputDirectory);

        AppBuilder.Configure<ScreenshotApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        Capture(outputDirectory, "01-cai-dat-ket-noi-csdl.png", CreateWizardWindow(SetupWizardStep.ConnectDb, withConnectionOk: false));
        Capture(outputDirectory, "02-cai-dat-tai-khoan-quan-tri.png", CreateWizardWindow(SetupWizardStep.FirstAdmin, withConnectionOk: true));
        Capture(outputDirectory, "03-cai-dat-thong-tin-co-so.png", CreateWizardWindow(SetupWizardStep.FacilityInfo, withConnectionOk: true));
        Capture(outputDirectory, "04-dang-nhap.png", CreateLoginWindow());
        Capture(outputDirectory, "05-man-chinh.png", CreateMainWindow());

        Console.WriteLine($"Đã lưu ảnh vào {outputDirectory}");
    }

    private static Window CreateWizardWindow(SetupWizardStep step, bool withConnectionOk)
    {
        var viewModel = new SetupWizardViewModel(new UnusedSetupService())
        {
            Host = "localhost",
            Port = 5442,
            Database = "lis",
            Username = "lis_user",
            Password = "lis_dev_pw",
            WorkstationCode = "XN-01",
            ConnectionOk = withConnectionOk,
            ConnectionMessage = withConnectionOk ? "Kết nối thành công" : null,
            Step = step,
        };

        if (step == SetupWizardStep.FirstAdmin)
        {
            viewModel.AdminUsername = "quantri";
            viewModel.AdminFullName = "Nguyễn Văn Thử";
        }

        if (step == SetupWizardStep.FacilityInfo)
        {
            viewModel.FacilityName = "Khoa Xét nghiệm — Bệnh viện Thử";
            viewModel.FacilityAddress = "123 Đường Thử, Quận Thử";
            viewModel.FacilityPhone = "0900000001";
        }

        return new SetupWizardWindow { DataContext = viewModel };
    }

    private static Window CreateLoginWindow()
    {
        var authService = new AuthService(Db.Create("Host=localhost;Database=unused;Username=unused;Password=unused"), new PasswordHasher(), TimeProvider.System);
        var viewModel = new LoginViewModel(authService) { Username = "ktv.thu" };
        return new LoginWindow { DataContext = viewModel };
    }

    private static Window CreateMainWindow()
    {
        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Nguyễn Văn Thử", "Kỹ thuật viên trưởng", [Roles.QuanTri], true);
        var viewModel = new MainViewModel(new CurrentUser(user));
        return new MainWindow { DataContext = viewModel };
    }

    private static void Capture(string outputDirectory, string fileName, Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(outputDirectory, fileName), new PngBitmapEncoderOptions());

        window.Close();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LisConnect.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Không tìm được gốc repo (LisConnect.slnx).");
    }

    /// <summary>ISetupService giả — các màn chụp ảnh không gọi tới CSDL thật.</summary>
    private sealed class UnusedSetupService : ISetupService
    {
        public Task<ConnectionTest> TestAsync(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không thử kết nối thật.");

        public MigrationResult Migrate(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh, không chạy migration thật.");

        public Task<bool> NeedsFirstAdminAsync(DbSettings settings) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");

        public Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password) =>
            throw new NotSupportedException("Chỉ dùng để chụp ảnh.");
    }
}

/// <summary>Application tối giản cho công cụ chụp ảnh: chỉ nạp FluentTheme + Theme.axaml của Lis.App.</summary>
internal sealed class ScreenshotApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Lis.Screenshots/"))
        {
            Source = new Uri("avares://Lis.App/Styles/Theme.axaml"),
        });
    }
}
