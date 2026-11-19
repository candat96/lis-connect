using System.Text.Json;
using Lis.Core.Agents;

namespace Lis.Agent;

/// <summary>Xử lý một lệnh của web trên agent. Ném <see cref="AgentCommandException"/> cho lỗi cần báo lên web.</summary>
public interface IAgentCommandHandler
{
    /// <summary>Tên lệnh trên dây (<c>reload-config</c>, <c>restart</c>, …).</summary>
    string Name { get; }

    /// <summary>Chạy lệnh; trả dữ liệu kèm kết quả (hoặc null).</summary>
    Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct);
}

/// <summary>
/// Lệnh cần làm tiếp <b>sau khi</b> kết quả đã gửi lên server (thu hồi, khởi động lại — làm trước thì web không nhận được kết quả).
/// Chỉ chạy khi lệnh thành công.
/// </summary>
public interface IAfterResultHandler : IAgentCommandHandler
{
    Task AfterResultAsync(CancellationToken ct);
}

/// <summary>Lỗi đã biết của lệnh: <see cref="Exception.Message"/> được báo nguyên văn lên web.</summary>
public sealed class AgentCommandException(string message) : Exception(message);

/// <summary>Chạy lệnh từ server: tuần tự (một lệnh một lúc), gửi <c>started</c> trước, trả <c>result</c> ok / lỗi.</summary>
public sealed class CommandDispatcher(IEnumerable<IAgentCommandHandler> handlers, TimeProvider? clock = null)
{
    /// <summary>Lệnh đợi lượt quá lâu (sau một lệnh dài) thì không chạy nữa — web đã coi là quá hạn, chạy muộn dễ gây nhầm.</summary>
    public static readonly TimeSpan MaxQueueWait = TimeSpan.FromSeconds(90);

    public const string QueueTimeoutMessage = "Agent bận — lệnh chờ quá 90 giây, không chạy";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly Dictionary<string, IAgentCommandHandler> _handlers =
        handlers.ToDictionary(h => h.Name, StringComparer.Ordinal);

    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<ChannelEnvelope> RunAsync(ChannelEnvelope command, Func<ChannelEnvelope, Task> send, CancellationToken ct)
    {
        var received = _clock.GetTimestamp();
        await _one.WaitAsync(ct);
        try
        {
            if (_clock.GetElapsedTime(received) > MaxQueueWait)
            {
                return Failed(command, QueueTimeoutMessage);
            }

            try
            {
                await send(new ChannelEnvelope(ChannelProtocol.Started, command.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kênh đứt giữa chừng: lệnh vẫn chạy, kết quả gửi được hay không do nơi gọi xử lý.
            }

            var name = command.Name ?? "";
            if (!_handlers.TryGetValue(name, out var handler))
            {
                return Failed(command, $"Agent chưa hỗ trợ lệnh {name}");
            }

            try
            {
                var data = handler is ICommandIdAware aware && command.Id is { } commandId
                    ? await aware.RunAsync(commandId, command.Args, ct)
                    : await handler.RunAsync(command.Args, ct);
                return new ChannelEnvelope(ChannelProtocol.Result, command.Id, Ok: true, Data: data);
            }
            catch (AgentCommandException ex)
            {
                return Failed(command, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed(command, ex.Message);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Làm tiếp việc sau khi kết quả đã gửi (nếu lệnh có <see cref="IAfterResultHandler"/>). Lỗi không ném ra.</summary>
    public async Task AfterResultAsync(ChannelEnvelope command, CancellationToken ct)
    {
        if (command.Name is { } name && _handlers.TryGetValue(name, out var handler) && handler is IAfterResultHandler after)
        {
            await after.AfterResultAsync(ct);
        }
    }

    private static ChannelEnvelope Failed(ChannelEnvelope command, string error) =>
        new(ChannelProtocol.Result, command.Id, Ok: false, Error: error);
}
