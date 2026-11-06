using Avalonia;
using Lis.App.Services;

namespace Lis.App;

/// <summary>
/// Điểm vào của ứng dụng LIS Connect.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Bắt lỗi ở hai tầng ngoài vòng lặp UI của Avalonia (lỗi trên luồng nền, task không ai
        // await) — nếu không bắt, .NET mặc định crash tiến trình ngay không kịp ghi gì. Lỗi
        // trên luồng UI (Dispatcher) được bắt riêng trong App.OnFrameworkInitializationCompleted
        // vì Dispatcher.UIThread chỉ sẵn sàng sau khi Avalonia khởi tạo.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Append("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "lỗi không rõ"));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Append("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
