using System.Text.Json;
using Lis.Connections;
using Lis.Data.Instruments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>Cách agent tự dừng / tự chạy lại — tách ra để lệnh <c>restart</c>, <c>revoke</c> kiểm thử được mà không thoát tiến trình thật.</summary>
public interface IAgentLifetime
{
    /// <summary>Khởi động lại agent: thoát sau khoảng 1 giây với mã lỗi để Windows (Recovery) bật lại dịch vụ. Trả về ngay.</summary>
    void RestartSoon();

    /// <summary>Dừng agent hẳn sau khi bị thu hồi (thoát mã 0).</summary>
    void StopForRevoke();
}

/// <summary>Bản thật: <c>RestartSoon</c> = sau 1 giây <c>Environment.Exit(1)</c> (đủ để kết quả lệnh tới server); <c>StopForRevoke</c> = dừng host.</summary>
public sealed class HostAgentLifetime(IHostApplicationLifetime application, ILogger<HostAgentLifetime> logger) : IAgentLifetime
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    public void RestartSoon()
    {
        logger.LogWarning("Khởi động lại agent theo lệnh từ web");
        _ = Task.Run(async () =>
        {
            await Task.Delay(RestartDelay);
            Environment.Exit(1);
        });
    }

    public void StopForRevoke()
    {
        logger.LogWarning("Agent đã bị thu hồi — dừng");
        application.StopApplication();
    }
}

/// <summary>
/// Trạng thái chạy chung giữa <see cref="AgentWorker"/> và các lệnh: bộ quản lý kết nối máy (dựng sau khi biết danh tính),
/// danh sách máy đang biết, cờ đã thu hồi. Lệnh đọc <see cref="Manager"/> lúc chạy nên đăng ký trước khi manager tồn tại cũng được.
/// </summary>
public sealed class AgentRuntime
{
    private volatile IInstrumentConnections? _manager;
    private volatile string? _workstationCode;
    private volatile IReadOnlyList<InstrumentInfo> _instruments = [];
    private volatile bool _revoked;

    public IInstrumentConnections? Manager
    {
        get => _manager;
        set => _manager = value;
    }

    /// <summary>Mã máy trạm của agent (khớp <c>instrument.workstation_code</c>).</summary>
    public string? WorkstationCode
    {
        get => _workstationCode;
        set => _workstationCode = value;
    }

    /// <summary>Danh sách máy lần cuối lấy được từ server (hoặc từ bản lưu).</summary>
    public IReadOnlyList<InstrumentInfo> Instruments
    {
        get => _instruments;
        set => _instruments = value;
    }

    /// <summary>Đã thu hồi: vòng chính không nạp lại / ghi bộ đệm nữa.</summary>
    public bool Revoked
    {
        get => _revoked;
        set => _revoked = value;
    }

    /// <summary>Các máy gán cho agent này.</summary>
    public IReadOnlyList<InstrumentInfo> Owned() =>
        _instruments.Where(i => string.Equals(i.WorkstationCode, _workstationCode, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Bộ quản lý kết nối máy; agent chưa dựng xong thì báo lỗi tiếng Việt.</summary>
    public IInstrumentConnections RequireManager() =>
        _manager ?? throw new AgentCommandException("Agent đang khởi động, chưa sẵn sàng nhận lệnh");
}

internal static class CommandJson
{
    public static JsonElement? Of<T>(T value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary><c>reload-config</c>: đọc lại danh sách máy từ server rồi mở / đóng kết nối cho khớp.</summary>
public sealed class ReloadConfigHandler(AgentRuntime runtime) : IAgentCommandHandler
{
    public string Name => "reload-config";

    public async Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct)
    {
        await runtime.RequireManager().ReloadAsync();
        return CommandJson.Of(new { instruments = runtime.Owned().Count });
    }
}

/// <summary><c>reconnect-instrument</c> <c>{instrumentId}</c>: đóng rồi mở lại kết nối một máy của agent.</summary>
public sealed class ReconnectInstrumentHandler(AgentRuntime runtime) : IAgentCommandHandler
{
    public string Name => "reconnect-instrument";

    public async Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct)
    {
        var manager = runtime.RequireManager();
        if (args is not { ValueKind: JsonValueKind.Object } a
            || !a.TryGetProperty("instrumentId", out var idText)
            || idText.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idText.GetString(), out var id))
        {
            throw new AgentCommandException("Thiếu mã máy cần kết nối lại");
        }

        if (runtime.Owned().All(i => i.Id != id))
        {
            throw new AgentCommandException("Máy này không chạy trên agent");
        }

        await manager.RestartAsync(id);
        return null;
    }
}

/// <summary>
/// <c>flush-pending</c>: gửi ngay các bản tin thô đang nằm trong hàng chờ trên đĩa; trả <c>{remaining}</c>. Vòng nền đang gửi mà quá
/// <see cref="RawMessageUploader.FlushWait"/> chưa xong thì trả ngay kèm <c>busy: true</c> (không giữ kênh lệnh).
/// </summary>
public sealed class FlushPendingHandler(RawMessageUploader uploader) : IAgentCommandHandler
{
    public string Name => "flush-pending";

    public async Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct)
    {
        var result = await uploader.FlushNowAsync(ct);
        return result.Busy
            ? CommandJson.Of(new { remaining = result.Remaining, busy = true })
            : CommandJson.Of(new { remaining = result.Remaining });
    }
}

/// <summary>
/// <c>restart</c>: chỉ khi chạy như dịch vụ (có Windows Recovery bật lại) — chạy console thì thoát là mất agent hẳn nên báo lỗi.
/// Trả kết quả trước; rồi đóng êm kết nối máy (tối đa <see cref="StopTimeout"/>, để không mất kết quả đang ghi) và mới khởi động lại.
/// </summary>
public sealed class RestartHandler(AgentRuntime runtime, IAgentLifetime lifetime, bool runningAsService, ILogger<RestartHandler>? logger = null)
    : IAfterResultHandler
{
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    public string Name => "restart";

    public Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct) =>
        runningAsService
            ? Task.FromResult<JsonElement?>(null)
            : throw new AgentCommandException("Agent không chạy như dịch vụ nên không tự khởi động lại");

    public async Task AfterResultAsync(CancellationToken ct)
    {
        try
        {
            if (runtime.Manager is { } manager)
            {
                await manager.StopAllAsync().WaitAsync(StopTimeout, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogWarning("Đóng kết nối máy trước khi khởi động lại chưa xong: {Message}", ex.Message);
        }

        lifetime.RestartSoon();
    }
}

/// <summary>
/// <c>revoke</c>: trả kết quả trước; rồi dừng kết nối máy, xoá khoá + bộ đệm (<c>pairing.json</c>, <c>agent.json</c>,
/// <c>instruments.json</c>, khoá dev), tắt tự chạy của dịch vụ Windows và dừng agent. Hàng chờ <c>cho-gui</c> giữ nguyên.
/// </summary>
/// <param name="service">Điều khiển dịch vụ Windows; null khi không phải Windows.</param>
/// <param name="runningAsService">Agent đang chạy như dịch vụ Windows thì mới tắt tự chạy.</param>
public sealed class RevokeHandler(
    AgentRuntime runtime, AgentPaths paths, IAgentLifetime lifetime, IServiceControl? service, bool runningAsService, ILogger<RevokeHandler> logger)
    : IAfterResultHandler
{
    public string Name => "revoke";

    public Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct) => Task.FromResult<JsonElement?>(null);

    public async Task AfterResultAsync(CancellationToken ct)
    {
        runtime.Revoked = true;
        try
        {
            if (runtime.Manager is { } manager)
            {
                await manager.StopAllAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Dừng kết nối máy khi thu hồi lỗi: {Message}", ex.Message);
        }

        foreach (var file in new[] { paths.Pairing, paths.IdentityCache, paths.InstrumentsCache, paths.DevKey })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Không xoá được {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
        }

        if (runningAsService && service is not null)
        {
            try
            {
                service.Disable();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Không tắt được tự chạy của dịch vụ: {Message}", ex.Message);
            }
        }

        lifetime.StopForRevoke();
    }
}
