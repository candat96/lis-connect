using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Lis.Agent;

/// <summary>Điều khiển dịch vụ Windows "LIS Agent" — tách ra để logic cài đặt kiểm thử được trên máy không phải Windows.</summary>
public interface IServiceControl
{
    bool Exists();

    bool IsRunning();

    /// <summary>
    /// Tạo dịch vụ trỏ tới <paramref name="exePath"/>: tự chạy khi bật máy; khi lỗi khởi động lại sau 5 s, 5 s, 30 s
    /// (đặt lại bộ đếm sau 1 ngày), kể cả khi thoát với mã lỗi; có mô tả tiếng Việt.
    /// </summary>
    void Create(string exePath);

    /// <summary>Đường dẫn tệp chạy của dịch vụ đang có (đã bỏ ngoặc kép / tham số); không đọc được thì null.</summary>
    string? BinPath();

    /// <summary>Trỏ dịch vụ đang có sang <paramref name="exePath"/> (sc config binPath=).</summary>
    void SetBinPath(string exePath);

    void Start();

    /// <summary>Dừng và chờ dịch vụ dừng hẳn (tệp chạy mới xoá / thay được).</summary>
    void Stop();

    void Delete();

    /// <summary>Tắt chế độ tự chạy của dịch vụ (<c>sc config "LIS Agent" start= disabled</c>) — dùng khi agent bị thu hồi để không tự bật lại sau khi tắt máy.</summary>
    void Disable();

    bool WaitRunning(TimeSpan timeout);
}

/// <summary>Thực hiện bằng <c>sc.exe</c> (tạo / cấu hình / xoá) và <see cref="ServiceController"/> (trạng thái). Chỉ chạy trên Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class ScServiceControl : IServiceControl
{
    public const string ServiceName = "LIS Agent";
    private const string Description = "Nhận kết quả từ máy xét nghiệm cạnh máy này và gửi lên máy chủ LIS; lấy chỉ định về trả lời cho máy.";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    public bool Exists() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    public bool IsRunning()
    {
        using var controller = new ServiceController(ServiceName);
        return controller.Status != ServiceControllerStatus.Stopped;
    }

    public void Create(string exePath)
    {
        // Mỗi phần tử là một tham số riêng; sc.exe đòi "binPath=" và giá trị tách rời nhau (có khoảng trắng sau dấu =).
        // Đường dẫn nằm trong ngoặc kép để dịch vụ không dính lỗi đường dẫn có khoảng trắng.
        Sc("create", ServiceName, "binPath=", $"\"{exePath}\"", "start=", "auto", "DisplayName=", ServiceName);
        try
        {
            Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
            Sc("failureflag", ServiceName, "1");
            Sc("description", ServiceName, Description);
        }
        catch (InvalidOperationException)
        {
            // Không để lại dịch vụ cấu hình dở.
            Sc("delete", ServiceName);
            throw;
        }
    }

    public string? BinPath()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key?.GetValue("ImagePath") is string image ? ExePathOf(image) : null;
    }

    public void SetBinPath(string exePath) => Sc("config", ServiceName, "binPath=", $"\"{exePath}\"");

    /// <summary>ImagePath có thể là <c>"C:\a b\x.exe" run</c>, <c>C:\a\x.exe</c> hoặc <c>C:\a\x.exe run</c>.</summary>
    internal static string ExePathOf(string imagePath)
    {
        var text = imagePath.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? text[1..] : text[1..end];
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? text : text[..(exe + 4)];
    }

    public void Start()
    {
        using var controller = new ServiceController(ServiceName);
        controller.Start();
    }

    public void Stop()
    {
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status != ServiceControllerStatus.StopPending)
        {
            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
    }

    public void Delete() => Sc("delete", ServiceName);

    public void Disable() => Sc("config", ServiceName, "start=", "disabled");

    public bool WaitRunning(TimeSpan timeout)
    {
        using var controller = new ServiceController(ServiceName);
        try
        {
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    private static void Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Không chạy được sc.exe");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc {arguments[0]} lỗi (mã {process.ExitCode}): {output.Trim()}");
        }
    }
}
