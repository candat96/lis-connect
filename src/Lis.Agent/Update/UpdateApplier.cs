using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lis.Core.Agents;

namespace Lis.Agent;

/// <summary>Bật một tiến trình tách rời (không chết theo dịch vụ khi dịch vụ dừng) — tách ra để kiểm thử không chạy tiến trình thật.</summary>
public interface IProcessLauncher
{
    void StartDetached(string exePath, string arguments, string workingDirectory);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public void StartDetached(string exePath, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        });
        if (process is null)
        {
            throw new InvalidOperationException("Không bật được tiến trình cập nhật");
        }
    }
}

/// <summary>
/// Việc của tiến trình <c>LisAgent.new.exe apply-update &lt;commandId&gt;</c> (chạy tách rời, quyền SYSTEM, con của dịch vụ cũ):
/// dừng dịch vụ → đổi <c>LisAgent.exe</c> thành <c>LisAgent.old.exe</c> → <b>chép</b> bản mới vào chỗ (không di chuyển: chính nó đang chạy từ
/// <c>LisAgent.new.exe</c>) → bật dịch vụ → chờ bản mới ghi <c>ket-noi.json</c> (đúng phiên bản, sau lúc bắt đầu) tối đa
/// <paramref name="connectTimeout"/>. Bất kỳ bước nào hỏng hoặc quá hạn: dừng dịch vụ (bỏ qua lỗi), trả <c>LisAgent.old.exe</c> về, bật lại dịch vụ.
/// Kết quả ghi vào <c>cap-nhat.json</c> để agent gửi kèm <c>hello</c> lần nối sau.
/// </summary>
public sealed class UpdateApplier(
    string installedExe, string newExe, AgentPaths paths, IServiceControl service, TimeProvider clock, TimeSpan connectTimeout,
    Action<string>? log = null, Action<string, string>? copyFile = null, Action<string>? deleteAfterExit = null, TimeSpan? pollInterval = null)
{
    public const string ConnectTimeoutMessage = "Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ";
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _oldExe = Path.Combine(Path.GetDirectoryName(installedExe)!, "LisAgent.old.exe");
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly Action<string, string> _copy = copyFile ?? ((from, to) => File.Copy(from, to, overwrite: true));

    public async Task<UpdateReport> ApplyAsync(Guid commandId, string newVersion, CancellationToken ct)
    {
        UpdateReport report;
        try
        {
            report = await ApplyCoreAsync(commandId, newVersion, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report = new UpdateReport(commandId, false, null, $"Cập nhật gặp lỗi không lường trước: {ex.Message}");
        }

        WriteReport(report);
        if (report.Ok)
        {
            TryDelete(_oldExe);
        }

        TryDeferDelete();
        return report;
    }

    private async Task<UpdateReport> ApplyCoreAsync(Guid commandId, string newVersion, CancellationToken ct)
    {
        // Chưa đụng gì tới dịch vụ: thiếu tệp thì dừng ngay, bản cũ vẫn chạy.
        if (!File.Exists(installedExe) || !File.Exists(newExe))
        {
            Log("Thiếu tệp cài hoặc tệp bản mới — không cập nhật");
            return new UpdateReport(commandId, false, null, "Thiếu tệp cài hoặc tệp bản mới — không cập nhật");
        }

        var moved = false;
        string? failure = null;
        try
        {
            var startedAt = clock.GetUtcNow();
            Log("Dừng dịch vụ");
            service.Stop();
            TryDelete(_oldExe);
            File.Move(installedExe, _oldExe);
            moved = true;
            Log("Chép bản mới vào chỗ");
            _copy(newExe, installedExe);
            Log("Bật dịch vụ bản mới");
            service.Start();

            if (await WaitConnectedAsync(newVersion, startedAt, ct))
            {
                Log($"Bản mới {newVersion} đã kết nối — cập nhật xong");
                return new UpdateReport(commandId, true, newVersion, null);
            }

            failure = ConnectTimeoutMessage;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex is OperationCanceledException
                ? "Cập nhật bị huỷ giữa chừng — đã quay về bản cũ"
                : $"Cài bản mới lỗi: {ex.Message} — đã quay về bản cũ";
            Log($"Lỗi: {ex.GetType().Name}: {ex.Message}");
        }

        Log("Quay về bản cũ");
        var restoreError = await RollbackAsync(moved);
        return new UpdateReport(commandId, false, null, restoreError is null ? failure : $"{failure}. Khôi phục bản cũ chưa xong: {restoreError}");
    }

    private async Task<bool> WaitConnectedAsync(string version, DateTimeOffset startedAt, CancellationToken ct)
    {
        var deadline = startedAt + connectTimeout;
        while (true)
        {
            if (Connected(version, startedAt))
            {
                return true;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(_poll, clock, ct);
        }
    }

    private bool Connected(string version, DateTimeOffset startedAt)
    {
        try
        {
            if (!File.Exists(paths.ConnectedMarker))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConnectedMarker));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == version
                   && doc.RootElement.TryGetProperty("at", out var at) && at.TryGetDateTimeOffset(out var when) && when >= startedAt;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            return false; // tệp đang được ghi dở — đọc lại lần sau
        }
    }

    /// <summary>Trả bản cũ về và bật lại dịch vụ. Trả thông báo lỗi nếu việc khôi phục không trọn vẹn (vẫn cố bật dịch vụ).</summary>
    private async Task<string?> RollbackAsync(bool moved)
    {
        string? error = null;
        try
        {
            service.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Dừng dịch vụ khi quay về lỗi (bỏ qua): {ex.Message}");
        }

        if (moved)
        {
            error = await RestoreOldAsync();
        }

        try
        {
            service.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Bật lại dịch vụ lỗi: {ex.Message}");
            error = error is null ? $"không bật lại được dịch vụ ({ex.Message})" : $"{error}; không bật lại được dịch vụ ({ex.Message})";
        }

        return error;
    }

    private async Task<string?> RestoreOldAsync()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(_oldExe, installedExe, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Log($"Trả bản cũ lần {attempt + 1} lỗi: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), clock);
            }
        }

        return $"không trả được LisAgent.old.exe về ({last!.Message})";
    }

    private void WriteReport(UpdateReport report)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var temp = paths.UpdateReport + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false));
            File.Move(temp, paths.UpdateReport, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Không ghi được cap-nhat.json: {ex.Message}");
        }
    }

    private void TryDeferDelete()
    {
        try
        {
            deleteAfterExit?.Invoke(newExe);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Không hẹn xoá {Path.GetFileName(newExe)}: {ex.Message}");
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Không xoá được {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private void Log(string message) => log?.Invoke(message);

    /// <summary>Tệp .cmd thử xoá <paramref name="file"/> mỗi giây tối đa 60 lần (tới khi tiến trình đang chạy từ nó thoát) rồi tự xoá.</summary>
    internal static string BuildDeleteFileScript(string file)
    {
        var path = file.Replace("%", "%%");
        return $"@echo off\r\ncd /d \"%TEMP%\"\r\nfor /l %%i in (1,1,60) do (\r\n  del /f /q \"{path}\" >nul 2>&1\r\n  if not exist \"{path}\" goto done\r\n  ping -n 2 127.0.0.1 >nul\r\n)\r\n:done\r\ndel \"%~f0\"\r\n";
    }
}
