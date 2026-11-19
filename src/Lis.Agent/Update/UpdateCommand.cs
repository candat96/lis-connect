using System.Text.Json;
using Lis.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>Lệnh cần mã lệnh của chính nó (vd <c>update</c> truyền mã cho tiến trình áp cập nhật).</summary>
public interface ICommandIdAware : IAgentCommandHandler
{
    Task<JsonElement?> RunAsync(Guid commandId, JsonElement? args, CancellationToken ct);
}

/// <summary>
/// <c>update</c> <c>{version, sha256}</c>: tải bản mới về <c>LisAgent.new.exe</c> cạnh tệp đang chạy, kiểm SHA-256 (sai → xoá, không đụng
/// tệp đang chạy), rồi — sau khi kết quả <c>ok {handedOver:true}</c> đã gửi — bật tách rời <c>LisAgent.new.exe apply-update &lt;commandId&gt;</c>.
/// </summary>
/// <param name="download">Tải về đường dẫn đích; trả (SHA-256 tính trên tệp đã ghi, phiên bản server báo).</param>
/// <param name="installDir">Thư mục của tệp đang chạy.</param>
public sealed class UpdateHandler(
    Func<string, CancellationToken, Task<(string Sha256, string? Version)>> download, string installDir, string currentVersion,
    bool runningAsService, IProcessLauncher launcher, AgentPaths paths, IAgentLifetime lifetime, ILogger<UpdateHandler>? logger = null) : IAfterResultHandler, ICommandIdAware
{
    public const string NewFileName = "LisAgent.new.exe";
    public const string ShaMismatchMessage = "Tệp cập nhật sai mã kiểm tra — không cài";

    private readonly object _gate = new();
    private (Guid CommandId, string NewExe)? _pending;

    public string Name => "update";

    public Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct) =>
        throw new AgentCommandException("Lệnh cập nhật thiếu mã lệnh");

    public async Task<JsonElement?> RunAsync(Guid commandId, JsonElement? args, CancellationToken ct)
    {
        if (args is not { ValueKind: JsonValueKind.Object } a
            || !TryText(a, "version", out var version) || !TryText(a, "sha256", out var sha256))
        {
            throw new AgentCommandException("Lệnh cập nhật thiếu phiên bản hoặc mã kiểm tra");
        }

        if (version == currentVersion)
        {
            return CommandJson.Of(new { alreadyCurrent = true });
        }

        if (!runningAsService)
        {
            throw new AgentCommandException("Chỉ cập nhật được agent cài dạng dịch vụ");
        }

        var newExe = Path.Combine(installDir, NewFileName);
        try
        {
            string computed;
            try
            {
                (computed, _) = await download(newExe, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AgentCommandException($"Không tải được bản cập nhật: {ex.Message}");
            }

            if (!string.Equals(computed, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentCommandException(ShaMismatchMessage);
            }
        }
        catch
        {
            TryDelete(newExe);
            throw;
        }

        lock (_gate)
        {
            _pending = (commandId, newExe);
        }

        return CommandJson.Of(new { handedOver = true });
    }

    /// <summary>
    /// Bật bộ áp cập nhật sau khi kết quả đã gửi. Không bật được: xoá tệp mới (chưa đụng gì tới tệp chạy), ghi <c>cap-nhat.json</c> báo lỗi rồi
    /// khởi động lại agent (<see cref="IAgentLifetime.RestartSoon"/>) — kênh đã gửi <c>handedOver</c> nên chỉ <c>hello</c> của lần nối sau
    /// mới đưa được báo lỗi lên web; Windows Recovery bật lại dịch vụ sau vài giây.
    /// </summary>
    public Task AfterResultAsync(CancellationToken ct)
    {
        (Guid CommandId, string NewExe)? taken;
        lock (_gate)
        {
            taken = _pending;
            _pending = null;
        }

        if (taken is not { } job)
        {
            return Task.CompletedTask;
        }

        var (commandId, newExe) = job;

        try
        {
            launcher.StartDetached(newExe, $"apply-update {commandId}", Path.GetTempPath());
            logger?.LogWarning("Đã giao việc cập nhật cho {Exe}", NewFileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError("Không bật được tiến trình cập nhật: {Message}", ex.Message);
            TryDelete(newExe);
            UpdateApplier.WriteReport(paths, new UpdateReport(commandId, false, null, $"Không bật được tiến trình cập nhật: {ex.Message}"), null);
            lifetime.RestartSoon();
        }

        return Task.CompletedTask;
    }

    private static bool TryText(JsonElement a, string name, out string value)
    {
        value = a.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        return value.Length > 0;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
