using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Lis.Core.Agents;
using Lis.Server.Tenancy;

namespace Lis.Server.AgentApi;

/// <summary>
/// Giữ các kênh lệnh WebSocket đang mở (agentId → kết nối; một tiến trình server) và ghi vòng đời lệnh vào <see cref="AgentCommandStore"/>.
/// Mỗi agent tối đa một kênh: kênh mới thay kênh cũ. Không ghi log nội dung <c>args</c>/<c>data</c> và không bao giờ ghi khoá agent.
/// </summary>
public sealed class AgentChannelHub(AgentCommandStore commands, MasterStore master, TimeProvider clock, ILogger<AgentChannelHub> logger)
{
    /// <summary>Lệnh đợi agent tối đa chừng này; quá thì hết hạn (<c>expired</c>).</summary>
    public static readonly TimeSpan QueueTtl = TimeSpan.FromSeconds(60);

    private const string ReplacedReason = "Có kết nối mới hơn";
    private const int MaxMachineName = 100;
    private const int MaxAgentVersion = 50;

    private readonly ConcurrentDictionary<Guid, ChannelConnection> _connections = new();
    private readonly SemaphoreSlim _registerLock = new(1, 1);

    /// <summary>
    /// Gọi sau khi một <c>result</c> đã ghi vào lịch sử lệnh (cho task lưu kết quả riêng, vd cổng COM sau <c>scan-ports</c>).
    /// Lỗi trong hàm này được ghi log và bỏ qua, không làm đứt kênh.
    /// </summary>
    public Func<Guid, ChannelEnvelope, Task>? ResultObserver { get; set; }

    public bool IsConnected(Guid agentId) => _connections.ContainsKey(agentId);

    /// <summary>
    /// Ghi lệnh mới: agent đang nối → ghi <c>sent</c> rồi gửi (gửi không được → <c>unknown</c>); chưa nối → ghi <c>queued</c>,
    /// giao khi agent nối (≤ 60 giây).
    /// </summary>
    public async Task<AgentCommandRow> SubmitAsync(Guid clinicId, Guid agentId, string name, string? argsJson, Guid? userId, string? userName)
    {
        var connection = _connections.GetValueOrDefault(agentId);
        var now = clock.GetUtcNow();
        if (connection is null)
        {
            var queued = await commands.CreateAsync(clinicId, agentId, name, argsJson, "queued", userId, userName, now);
            // Agent vừa nối giữa lúc ghi: giao ngay thay vì đợi hello lần sau (chuyển queued → sent là nguyên tử, không giao đôi).
            if (_connections.GetValueOrDefault(agentId) is { } late)
            {
                await DeliverQueuedAsync(late, agentId);
                return await commands.FindAsync(clinicId, queued.Id) ?? queued;
            }

            return queued;
        }

        var row = await commands.CreateAsync(clinicId, agentId, name, argsJson, "sent", userId, userName, now);
        if (await connection.SendAsync(ToCommand(row)))
        {
            logger.LogInformation("Gửi lệnh {Name} ({CommandId}) tới agent {AgentId}", row.Name, row.Id, agentId);
            return row;
        }

        await commands.TransitionAsync(row.Id, ["sent"], "unknown", clock.GetUtcNow(), error: "Agent mất kết nối giữa chừng");
        return await commands.FindAsync(clinicId, row.Id) ?? row;
    }

    /// <summary>Báo agent đang nối tải lại cấu hình (không ghi lịch sử lệnh). Agent chưa nối thì bỏ qua — lúc nối nó tự đọc cấu hình mới.</summary>
    public async Task NotifyConfigChangedAsync(IEnumerable<Guid> agentIds)
    {
        foreach (var agentId in agentIds.Distinct())
        {
            if (_connections.GetValueOrDefault(agentId) is { } connection)
            {
                await connection.SendAsync(new ChannelEnvelope(ChannelProtocol.ConfigChanged));
            }
        }
    }

    /// <summary>Đóng kênh của agent (thu hồi). Lệnh đang dở được xử lý khi vòng nhận kết thúc.</summary>
    public async Task DisconnectAsync(Guid agentId)
    {
        if (_connections.GetValueOrDefault(agentId) is { } connection)
        {
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Khoá agent đã bị thu hồi");
        }
    }

    /// <summary>Chạy suốt đời một kênh: đăng ký (thay kênh cũ), vòng nhận, dọn khi đóng. Trả về khi kênh đã đóng.</summary>
    public async Task RunAsync(AgentIdentity identity, WebSocket socket, CancellationToken ct)
    {
        var connection = new ChannelConnection(socket);
        await RegisterAsync(identity.Agent.Id, connection);
        try
        {
            await ReceiveLoopAsync(identity, connection, ct);
        }
        finally
        {
            // So khớp rồi mới gỡ: nếu kênh mới đã thay chỗ thì không đụng tới lệnh của kênh mới.
            if (_connections.TryRemove(new KeyValuePair<Guid, ChannelConnection>(identity.Agent.Id, connection)))
            {
                await MarkDisconnectedSafeAsync(identity.Agent.Id);
            }

            await connection.FinishAsync();
        }
    }

    private async Task RegisterAsync(Guid agentId, ChannelConnection connection)
    {
        await _registerLock.WaitAsync();
        try
        {
            if (_connections.TryRemove(agentId, out var old))
            {
                // Lệnh đã gửi vào kênh cũ không còn ai trả lời. Làm trước khi kênh mới nhận lệnh nào nên không đụng lệnh mới.
                await MarkDisconnectedSafeAsync(agentId);
                await old.CloseAsync(WebSocketCloseStatus.NormalClosure, ReplacedReason);
            }

            _connections[agentId] = connection;
        }
        finally
        {
            _registerLock.Release();
        }
    }

    private async Task MarkDisconnectedSafeAsync(Guid agentId)
    {
        try
        {
            var count = await commands.MarkDisconnectedAsync(agentId, clock.GetUtcNow());
            if (count > 0)
            {
                logger.LogWarning("Agent {AgentId} mất kết nối khi {Count} lệnh còn dở", agentId, count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không ghi được việc agent {AgentId} mất kết nối", agentId);
        }
    }

    private async Task ReceiveLoopAsync(AgentIdentity identity, ChannelConnection connection, CancellationToken ct)
    {
        var socket = connection.Socket;
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Đóng kênh");
                        return;
                    }

                    if (message.Length + result.Count > ChannelProtocol.MaxMessageBytes)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Khung quá lớn");
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var envelope = result.MessageType == WebSocketMessageType.Text ? ChannelProtocol.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) : null;
                if (envelope is null)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Khung không hợp lệ");
                    return;
                }

                await HandleAsync(identity, connection, envelope);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Agent đứt đột ngột / server tắt — dọn ở finally của RunAsync.
        }
    }

    private async Task HandleAsync(AgentIdentity identity, ChannelConnection connection, ChannelEnvelope envelope)
    {
        var agentId = identity.Agent.Id;
        try
        {
            switch (envelope.Type)
            {
                case ChannelProtocol.Hello:
                    await master.TouchAgentAsync(
                        agentId, null, AgentEndpoints.Clean(envelope.MachineName, MaxMachineName), AgentEndpoints.Clean(envelope.Version, MaxAgentVersion));
                    await DeliverQueuedAsync(connection, agentId);
                    break;
                case ChannelProtocol.Started when envelope.Id is { } id && await OwnsAsync(identity, id):
                    await commands.TransitionAsync(id, ["sent"], "running", clock.GetUtcNow());
                    break;
                case ChannelProtocol.Result when envelope.Id is { } id && await OwnsAsync(identity, id):
                    await FinishAsync(agentId, id, envelope);
                    break;
                default:
                    logger.LogWarning("Agent {AgentId} gửi khung {Type} không dùng được", agentId, envelope.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Lỗi CSDL tạm thời không được làm đứt kênh; chỉ ghi loại khung, không ghi nội dung.
            logger.LogError(ex, "Xử lý khung {Type} của agent {AgentId} thất bại", envelope.Type, agentId);
        }
    }

    private async Task FinishAsync(Guid agentId, Guid id, ChannelEnvelope envelope)
    {
        var ok = envelope.Ok == true;
        var error = ok ? null : string.IsNullOrWhiteSpace(envelope.Error) ? "Lệnh không thành công" : envelope.Error;
        var changed = await commands.TransitionAsync(
            id, ["sent", "running"], ok ? "done" : "failed", clock.GetUtcNow(), envelope.Data?.GetRawText(), error);
        logger.LogInformation("Lệnh {CommandId} của agent {AgentId} kết thúc: {Status}", id, agentId, ok ? "done" : "failed");
        if (changed && ResultObserver is { } observer)
        {
            try
            {
                await observer(agentId, envelope);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Xử lý kết quả lệnh {CommandId} thất bại", id);
            }
        }
    }

    /// <summary>Lệnh có thuộc agent này không (chặn agent đổi trạng thái lệnh của agent khác bằng cách đoán mã).</summary>
    private async Task<bool> OwnsAsync(AgentIdentity identity, Guid commandId) =>
        await commands.FindAsync(identity.Clinic.Id, commandId) is { } row && row.AgentId == identity.Agent.Id;

    private async Task DeliverQueuedAsync(ChannelConnection connection, Guid agentId)
    {
        var now = clock.GetUtcNow();
        foreach (var row in await commands.QueuedForAsync(agentId, now - QueueTtl))
        {
            // Ghi sent trước khi gửi; ai đổi được trước thì người đó giao (không giao đôi).
            if (!await commands.TransitionAsync(row.Id, ["queued"], "sent", now))
            {
                continue;
            }

            if (await connection.SendAsync(ToCommand(row)))
            {
                logger.LogInformation("Giao lệnh chờ {Name} ({CommandId}) tới agent {AgentId}", row.Name, row.Id, agentId);
            }
            else
            {
                await commands.TransitionAsync(row.Id, ["sent"], "unknown", clock.GetUtcNow(), error: "Agent mất kết nối giữa chừng");
            }
        }
    }

    private static ChannelEnvelope ToCommand(AgentCommandRow row) =>
        new(ChannelProtocol.Command, row.Id, row.Name, row.Args);

    /// <summary>Một kênh đang mở: mọi lần gửi đi qua một khoá để không chồng khung.</summary>
    private sealed class ChannelConnection(WebSocket socket)
    {
        private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocket Socket { get; } = socket;

        /// <summary>Gửi một khung; kênh không mở hoặc lỗi gửi → false.</summary>
        public async Task<bool> SendAsync(ChannelEnvelope envelope)
        {
            var bytes = Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(envelope));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _sendLock.WaitAsync(timeout.Token);
                try
                {
                    if (Socket.State != WebSocketState.Open)
                    {
                        return false;
                    }

                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
                    return true;
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>Gửi khung đóng (không chờ bên kia); sau thời gian ân hạn thì ngắt hẳn để vòng nhận không treo.</summary>
        public async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseGrace);
                await _sendLock.WaitAsync(timeout.Token);
                try
                {
                    if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await Socket.CloseOutputAsync(status, reason, timeout.Token);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                Abort();
                return;
            }

            _ = Task.Delay(CloseGrace).ContinueWith(_ => Abort(), TaskScheduler.Default);
        }

        /// <summary>Kết thúc kênh: nếu mình đã gửi khung đóng thì chờ bên kia trả lời (tối đa ân hạn) rồi ngắt.</summary>
        public async Task FinishAsync()
        {
            if (Socket.State == WebSocketState.CloseSent)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(CloseGrace);
                    var buffer = new byte[1024];
                    while (Socket.State == WebSocketState.CloseSent)
                    {
                        await Socket.ReceiveAsync(buffer, timeout.Token);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }

            Abort();
        }

        public void Abort()
        {
            try
            {
                Socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
