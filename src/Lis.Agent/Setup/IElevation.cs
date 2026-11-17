using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Lis.Agent;

public enum ElevationResult
{
    /// <summary>Tiến trình nâng quyền đã chạy xong (mã thoát ở <c>exitCode</c>).</summary>
    Started,

    /// <summary>Người dùng bấm Không ở hộp thoại UAC.</summary>
    Cancelled,
}

/// <summary>Kiểm tra / xin quyền quản trị Windows (UAC).</summary>
public interface IElevation
{
    bool IsElevated();

    /// <summary>Chạy lại <paramref name="exePath"/> với quyền quản trị, chờ nó thoát và trả mã thoát của nó.</summary>
    ElevationResult RelaunchElevated(string exePath, string arguments, out int exitCode);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsElevation : IElevation
{
    private const int ErrorCancelled = 1223;

    public bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public ElevationResult RelaunchElevated(string exePath, string arguments, out int exitCode)
    {
        exitCode = 0;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = true, Verb = "runas" })
                                ?? throw new InvalidOperationException("Không chạy được tiến trình quản trị");
            process.WaitForExit();
            exitCode = process.ExitCode;
            return ElevationResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevationResult.Cancelled;
        }
    }
}
