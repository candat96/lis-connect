using System.Diagnostics;
using Lis.Core.Agents;

namespace Lis.Agent;

public abstract record InstallOutcome
{
    public sealed record Succeeded(PairingResult Pairing) : InstallOutcome;

    /// <summary>Không thành công; <see cref="Message"/> là câu tiếng Việt cho người dùng.</summary>
    public sealed record Failed(string Message) : InstallOutcome;

    public sealed record Uninstalled(string DataDirectory) : InstallOutcome;
}

/// <summary>
/// Cài / ghép nối lại / gỡ agent. Thứ tự cố định để lỗi ở bước nào cũng quay về được trạng thái trước đó:
/// (1) ghi nhớ + dừng dịch vụ cũ, (2) chép tệp (giữ <c>.bak</c>), (3) tạo dịch vụ, (4) đổi mã lấy khoá,
/// (5) bật dịch vụ. Lỗi ở 1–4 thì hoàn tác ngược; ở bước 5 mã đã tiêu và khoá đã lưu nên không hoàn tác.
/// </summary>
public sealed class AgentInstaller(
    AgentPaths paths,
    string installDir,
    IServiceControl service,
    Func<PairingCode, CancellationToken, Task<PairingResult>> pair,
    TextWriter output,
    TimeSpan? runningTimeout = null,
    string? runningExe = null,
    Action<string>? deferDelete = null)
{
    public const string ExeName = "LisAgent.exe";

    private readonly TimeSpan _runningTimeout = runningTimeout ?? TimeSpan.FromSeconds(30);

    public bool ServiceExists => service.Exists();

    private string Target => Path.Combine(installDir, ExeName);

    public async Task<InstallOutcome> InstallOrRepairAsync(string sourceExe, PairingCode code, CancellationToken ct)
    {
        var undo = new List<(string What, Action Run)>();
        try
        {
            // 1. Ghi nhớ trạng thái cũ, dừng dịch vụ đang chạy.
            var existed = service.Exists();
            var wasRunning = existed && service.IsRunning();
            if (wasRunning)
            {
                output.WriteLine("Đang dừng dịch vụ cũ...");
                service.Stop();
                undo.Add(("bật lại dịch vụ cũ", service.Start));
            }

            // 2. Chép tệp vào thư mục cài (trừ khi đang chạy chính từ đó).
            if (!SamePath(sourceExe, Target))
            {
                output.WriteLine($"Đang chép agent vào {installDir}...");
                undo.Add(("trả tệp cũ", CopyIntoInstallDir(sourceExe)));
            }

            // 3. Tạo dịch vụ nếu chưa có.
            if (!existed)
            {
                output.WriteLine("Đang tạo dịch vụ Windows \"LIS Agent\"...");
                service.Create(Target);
                undo.Add(("xoá dịch vụ vừa tạo", service.Delete));
            }

            // 4. Đổi mã lấy khoá (lưu khoá nằm trong delegate).
            output.WriteLine("Đang ghép nối với máy chủ...");
            var result = await pair(code, ct);

            // 5. Bật dịch vụ — từ đây không hoàn tác được nữa vì mã đã tiêu và khoá đã lưu.
            return StartAndWait(result, FinishCleanup);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = ex is OperationCanceledException ? "Đã huỷ cài đặt." : ex.Message;
            var problems = new List<string>();
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo[i].Run();
                }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
                {
                    problems.Add($"Không {undo[i].What} được ({rollbackError.Message})");
                }
            }

            return new InstallOutcome.Failed(problems.Count == 0
                ? $"{message} Máy giữ nguyên như trước khi cài."
                : $"{message} Hoàn tác chưa trọn vẹn: {string.Join("; ", problems)}.");
        }
    }

    private InstallOutcome StartAndWait(PairingResult result, Action finish)
    {
        const string Advice = "Xem nhật ký trong %ProgramData%\\LIS Agent, rồi chạy lại tệp cài, chọn Ghép nối lại với mã mới.";
        try
        {
            output.WriteLine("Đang khởi động dịch vụ...");
            service.Start();
            if (!service.WaitRunning(_runningTimeout))
            {
                return new InstallOutcome.Failed(
                    $"Đã ghép nối nhưng dịch vụ \"LIS Agent\" chưa chạy sau {_runningTimeout.TotalSeconds:0} giây. {Advice}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new InstallOutcome.Failed($"Đã ghép nối nhưng không bật được dịch vụ \"LIS Agent\" ({ex.Message}). {Advice}");
        }

        finish();
        return new InstallOutcome.Succeeded(result);
    }

    /// <summary>Chép tệp mới vào thư mục cài, giữ tệp cũ làm <c>.bak</c>; trả hành động hoàn tác.</summary>
    private Action CopyIntoInstallDir(string sourceExe)
    {
        var backup = Target + ".bak";
        var createdDir = !Directory.Exists(installDir);
        Directory.CreateDirectory(installDir);
        var hadOld = File.Exists(Target);
        Action undo = () =>
        {
            if (hadOld)
            {
                File.Move(backup, Target, overwrite: true);
            }
            else
            {
                File.Delete(Target);
                if (createdDir && Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
                {
                    Directory.Delete(installDir);
                }
            }
        };
        if (hadOld)
        {
            File.Copy(Target, backup, overwrite: true);
        }

        try
        {
            File.Copy(sourceExe, Target, overwrite: true);
        }
        catch
        {
            undo();
            throw;
        }

        return undo;
    }

    private void FinishCleanup()
    {
        try
        {
            File.Delete(Target + ".bak");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tệp .bak thừa không ảnh hưởng — bỏ qua.
        }
    }

    public InstallOutcome Uninstall()
    {
        try
        {
            if (service.Exists())
            {
                if (service.IsRunning())
                {
                    service.Stop();
                }

                service.Delete();
            }

            if (File.Exists(paths.Pairing))
            {
                File.Delete(paths.Pairing);
            }

            if (Directory.Exists(installDir))
            {
                if (runningExe is not null && IsUnder(runningExe, installDir))
                {
                    (deferDelete ?? DeleteAfterExit)(installDir);
                }
                else
                {
                    Directory.Delete(installDir, recursive: true);
                }
            }

            return new InstallOutcome.Uninstalled(paths.Root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new InstallOutcome.Failed($"Gỡ chưa xong: {ex.Message}");
        }
    }

    /// <summary>Đang chạy từ chính thư mục cài thì không xoá ngay được — hẹn xoá sau khi tiến trình này thoát.</summary>
    private static void DeleteAfterExit(string directory) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout 3 & rmdir /s /q \"{directory}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnder(string file, string directory) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
