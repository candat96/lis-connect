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
/// (1) <b>trước khi dừng dịch vụ</b> chép bản mới thành <c>LisAgent.tmp.exe</c> cạnh tệp cài và kiểm SHA-256 khớp bản mới;
/// (2) dừng dịch vụ → hoán đổi gần như nguyên tử bằng <c>File.Replace(tmp, exe, old)</c> (lỗi thì hai lần đổi tên exe→old, tmp→exe);
/// (3) bật dịch vụ → chờ bản mới ghi <c>ket-noi.json</c> (đúng phiên bản, sau lúc bắt đầu) tối đa <paramref name="connectTimeout"/>.
/// Hỏng sau khi đã dừng/đổi: dừng dịch vụ (bỏ qua lỗi), trả <c>LisAgent.old.exe</c> về (thử lại tới ~30 giây vì Recovery của Windows có thể
/// bật lại bản mới đang hỏng làm khoá tệp), <b>ghi <c>cap-nhat.json</c> rồi mới bật dịch vụ</b> để hello đầu tiên của bản cũ đã mang báo lỗi.
/// Hỏng ở bước chuẩn bị (chưa đổi gì): ghi báo lỗi rồi dừng + bật lại dịch vụ để agent đang chạy nối lại và gửi báo lỗi.
/// </summary>
public sealed class UpdateApplier(
    string installedExe, string newExe, AgentPaths paths, IServiceControl service, TimeProvider clock, TimeSpan connectTimeout,
    Action<string>? log = null, Action<string, string>? copyFile = null, Action<string>? deleteAfterExit = null, TimeSpan? pollInterval = null,
    Action<string, string, string>? replaceFile = null)
{
    public const string ConnectTimeoutMessage = "Bản mới không kết nối được máy chủ trong 2 phút — đã quay về bản cũ";
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMinutes(2);
    public const string OldFileName = "LisAgent.old.exe";
    public const string TempFileName = "LisAgent.tmp.exe";
    private const int RestoreAttempts = 15;
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _oldExe = Path.Combine(Path.GetDirectoryName(installedExe)!, OldFileName);
    private readonly string _tmpExe = Path.Combine(Path.GetDirectoryName(installedExe)!, TempFileName);
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly Action<string, string> _copy = copyFile ?? ((from, to) => File.Copy(from, to, overwrite: true));
    private readonly Action<string, string, string> _replace = replaceFile ?? ((tmp, exe, old) => File.Replace(tmp, exe, old));

    /// <summary>
    /// Máy bị ngắt giữa lúc đổi tệp (có <c>LisAgent.old.exe</c> mà không có <c>LisAgent.exe</c>): trả bản cũ về. Gọi lúc cài / sửa và đầu <c>apply-update</c>.
    /// </summary>
    /// <returns>true nếu đã khôi phục.</returns>
    public static bool RecoverMissingExe(string installedExe, Action<string>? log = null)
    {
        var old = Path.Combine(Path.GetDirectoryName(installedExe)!, OldFileName);
        try
        {
            if (File.Exists(installedExe) || !File.Exists(old))
            {
                return false;
            }

            File.Move(old, installedExe);
            log?.Invoke("Thiếu LisAgent.exe — đã trả LisAgent.old.exe về");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Không trả được LisAgent.old.exe về: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lỗi trước khi đổi tệp: ghi <c>cap-nhat.json</c> rồi dừng + bật lại dịch vụ (lỗi bỏ qua) để agent đang chạy nối lại và gửi báo lỗi lên web.
    /// Dùng được khi chưa dựng được <see cref="UpdateApplier"/> (vd không biết tệp cài của dịch vụ).
    /// </summary>
    public static UpdateReport FailEarly(Guid commandId, string error, AgentPaths paths, IServiceControl service, Action<string>? log = null)
    {
        log?.Invoke(error);
        var report = new UpdateReport(commandId, false, null, error);
        WriteReport(paths, report, log);
        try
        {
            service.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke($"Dừng dịch vụ để gửi báo lỗi lỗi (bỏ qua): {ex.Message}");
        }

        try
        {
            service.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke($"Bật lại dịch vụ lỗi: {ex.Message}");
        }

        return report;
    }

    public async Task<UpdateReport> ApplyAsync(Guid commandId, string newVersion, CancellationToken ct)
    {
        UpdateReport report;
        try
        {
            report = await ApplyCoreAsync(commandId, newVersion, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report = FailEarly(commandId, $"Cập nhật gặp lỗi không lường trước: {ex.Message}", paths, service, log);
        }

        WriteReport(paths, report, log);
        if (report.Ok)
        {
            TryDelete(_oldExe);
        }

        TryDelete(_tmpExe);
        TryDeferDelete();
        return report;
    }

    private async Task<UpdateReport> ApplyCoreAsync(Guid commandId, string newVersion, CancellationToken ct)
    {
        RecoverMissingExe(installedExe, log);
        if (!File.Exists(installedExe) || !File.Exists(newExe))
        {
            return FailEarly(commandId, "Thiếu tệp cài hoặc tệp bản mới — không cập nhật", paths, service, log);
        }

        // Chuẩn bị khi dịch vụ còn chạy: bản chép tạm đã kiểm SHA nằm sẵn cạnh tệp cài, lúc dừng chỉ còn đổi tên.
        try
        {
            TryDelete(_oldExe);
            TryDelete(_tmpExe);
            Log("Chép bản mới sang tệp tạm");
            _copy(newExe, _tmpExe);
            if (Sha256(_tmpExe) != Sha256(newExe))
            {
                throw new IOException("bản chép tạm không khớp bản mới");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(_tmpExe);
            return FailEarly(commandId, $"Chuẩn bị bản mới lỗi: {ex.Message} — giữ nguyên bản cũ", paths, service, log);
        }

        string failure;
        try
        {
            var startedAt = clock.GetUtcNow();
            Log("Dừng dịch vụ");
            service.Stop();
            Swap();
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
        return await RollbackAsync(commandId, failure);
    }

    /// <summary>tmp → exe, exe cũ → old. <c>File.Replace</c> trước; lỗi thì hai lần đổi tên (exe→old rồi tmp→exe).</summary>
    private void Swap()
    {
        try
        {
            _replace(_tmpExe, installedExe, _oldExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log($"File.Replace lỗi ({ex.Message}) — đổi tên hai bước");
            File.Move(installedExe, _oldExe, overwrite: true);
            File.Move(_tmpExe, installedExe);
        }
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
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

    /// <summary>Dừng dịch vụ, trả bản cũ về (nếu đã đổi), <b>ghi báo lỗi</b>, rồi mới bật lại dịch vụ.</summary>
    private async Task<UpdateReport> RollbackAsync(Guid commandId, string failure)
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

        if (File.Exists(_oldExe))
        {
            error = await RestoreOldAsync();
        }

        TryDelete(_tmpExe);
        var report = new UpdateReport(commandId, false, null, error is null ? failure : $"{failure}. Khôi phục bản cũ chưa xong: {error}");
        WriteReport(paths, report, log);

        try
        {
            service.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Bật lại dịch vụ lỗi: {ex.Message}");
            report = report with { Error = $"{report.Error}; không bật lại được dịch vụ ({ex.Message})" };
        }

        return report;
    }

    private async Task<string?> RestoreOldAsync()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < RestoreAttempts; attempt++)
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
                await Task.Delay(RestoreDelay, clock);
            }
        }

        return $"không trả được {OldFileName} về ({last!.Message})";
    }

    internal static void WriteReport(AgentPaths paths, UpdateReport report, Action<string>? log)
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
            log?.Invoke($"Không ghi được cap-nhat.json: {ex.Message}");
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
