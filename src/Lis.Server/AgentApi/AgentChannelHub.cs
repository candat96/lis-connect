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
    private const int MaxError = 2000;

    private readonly ConcurrentDictionary<Guid, ChannelConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _registerLocks = new();

    /// <summary>
    /// Gọi sau khi một <c>result</c> đã ghi vào lịch sử lệnh (cho task lưu kết quả riêng, vd cổng COM sau <c>scan-ports</c>):
    /// tham số là agent, dòng lệnh (đã có trạng thái mới, có <c>Name</c>) và phong bì kết quả. Kết quả tới sau khi lệnh đã sang
    /// <c>unknown</c>/<c>expired</c> bị bỏ, không gọi hàm này. Lỗi trong hàm được ghi log và bỏ qua, không làm đứt kênh.
    /// </summary>
    public Func<Guid, AgentCommandRow, ChannelEnvelope, Task>? ResultObserver { get; set; }

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

    /// <summary>Kênh đang giữ <paramref name="agentId"/> (null khi chưa nối) — dùng với <see cref="DisconnectAsync(Guid, object)"/> để chỉ đóng đúng kênh đó.</summary>
    public object? CurrentConnection(Guid agentId) => _connections.GetValueOrDefault(agentId);

    /// <summary>Đóng kênh của agent chỉ khi nó vẫn là <paramref name="expected"/>; agent đã nối lại bằng kênh mới thì không đụng tới.</summary>
    public async Task DisconnectAsync(Guid agentId, object expected)
    {
        if (_connections.GetValueOrDefault(agentId) is { } connection && ReferenceEquals(connection, expected))
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
            // So khớp rồi mới gỡ: nếu kênh mới đã thay chỗ thì không đụng tới lệnh của kênh mới. Giữ khoá đăng ký của agent suốt
            // "gỡ → đánh dấu": agent nối lại ngay lúc này phải chờ, nên không lệnh nào kịp gửi vào kênh mới rồi bị đánh dấu unknown
            // (MarkDisconnectedAsync tính theo agent, không theo kênh).
            var gate = _registerLocks.GetOrAdd(identity.Agent.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (_connections.TryRemove(new KeyValuePair<Guid, ChannelConnection>(identity.Agent.Id, connection)))
                {
                    await MarkDisconnectedSafeAsync(identity.Agent.Id);
                }
            }
            finally
            {
                gate.Release();
            }

            await connection.FinishAsync();
        }
    }

    private async Task RegisterAsync(Guid agentId, ChannelConnection connection)
    {
        var gate = _registerLocks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_connections.TryRemove(agentId, out var old))
            {
                // Thứ tự quan trọng: (1) khoá kênh cũ — từ đây mọi lần gửi vào nó trả false, SubmitAsync tự đánh dấu unknown;
                // (2) đóng; (3) đánh dấu các lệnh đã gửi vào kênh cũ. Làm trước khi kênh mới nhận lệnh nào nên không đụng lệnh mới.
                await old.CloseAsync(WebSocketCloseStatus.NormalClosure, ReplacedReason);
                await MarkDisconnectedSafeAsync(agentId);
            }

            _connections[agentId] = connection;
        }
        finally
        {
            gate.Release();
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
                    await SettleUpdateAsync(identity, envelope);
                    await DeliverQueuedAsync(connection, agentId);
                    break;
                case ChannelProtocol.Started when envelope.Id is { } id && await OwnsAsync(identity, id) is not null:
                    await commands.TransitionAsync(id, ["sent"], "running", clock.GetUtcNow());
                    break;
                case ChannelProtocol.Result when envelope.Id is { } id && await OwnsAsync(identity, id) is { } row:
                    await FinishAsync(identity, row, envelope);
                    break;
                default:
                    logger.LogWarning("Agent {AgentId} gửi khung không dùng được (loại {Type})", agentId, Truncate(envelope.Type, 40));
                    break;
            }
        }
        catch (Exception ex)
        {
            // Lỗi CSDL tạm thời không được làm đứt kênh; chỉ ghi loại khung, không ghi nội dung.
            logger.LogError(ex, "Xử lý khung {Type} của agent {AgentId} thất bại", envelope.Type, agentId);
        }
    }

    /// <summary>
    /// <c>hello</c> chốt lệnh <c>update</c> (agent tự khởi động lại giữa lệnh nên không có <c>result</c>): có <c>Update</c> → lệnh đó
    /// thành <c>done</c>/<c>failed</c> (từ sent/running/unknown), chỉ khi lệnh thuộc agent này; không có, hoặc báo cáo không chốt được lệnh nào,
    /// mà phiên bản đang chạy khớp <c>args.version</c> của lệnh update đang dở → <c>done</c>.
    /// </summary>
    private async Task SettleUpdateAsync(AgentIdentity identity, ChannelEnvelope hello)
    {
        var now = clock.GetUtcNow();
        string[] open = ["sent", "running", "unknown"];
        if (hello.Update is { } report && await OwnsAsync(identity, report.CommandId) is { Name: "update" } row)
        {
            var changed = report.Ok
                ? await commands.TransitionAsync(row.Id, open, "done", now, System.Text.Json.JsonSerializer.Serialize(new { version = report.Version }))
                : await commands.TransitionAsync(
                    row.Id, open, "failed", now, error: string.IsNullOrWhiteSpace(report.Error) ? "Cập nhật không thành công" : Truncate(report.Error, MaxError));
            logger.LogInformation("Lệnh cập nhật {CommandId} của agent {AgentId} chốt qua hello: {Status}", row.Id, identity.Agent.Id, changed ? (report.Ok ? "done" : "failed") : "bỏ qua");
            if (changed)
            {
                return;
            }
        }

        // Không có báo cáo, hoặc báo cáo không chốt được lệnh nào (lệnh đã xong / không phải update của agent này — vd báo cáo cũ còn sót):
        // vẫn so phiên bản đang chạy với lệnh update đang mở.
        var version = AgentEndpoints.Clean(hello.Version, MaxAgentVersion);
        if (version is null || await commands.OpenUpdateAsync(identity.Agent.Id, now) is not { } pending)
        {
            return;
        }

        if (pending.Args is { ValueKind: System.Text.Json.JsonValueKind.Object } args
            && args.TryGetProperty("version", out var expected) && expected.ValueKind == System.Text.Json.JsonValueKind.String
            && expected.GetString() == version)
        {
            await commands.TransitionAsync(pending.Id, open, "done", now, System.Text.Json.JsonSerializer.Serialize(new { version }));
            logger.LogInformation("Lệnh cập nhật {CommandId} của agent {AgentId} xong: agent báo đúng phiên bản", pending.Id, identity.Agent.Id);
        }
    }

    private async Task FinishAsync(AgentIdentity identity, AgentCommandRow row, ChannelEnvelope envelope)
    {
        // update trả ok {handedOver:true}: việc chưa xong (agent còn phải đổi tệp và nối lại) — giữ running, hello sau chốt done/failed.
        if (row.Name == "update" && envelope.Ok == true
            && envelope.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
            && data.TryGetProperty("handedOver", out var handed) && handed.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            await commands.TransitionAsync(row.Id, ["sent"], "running", clock.GetUtcNow());
            return;
        }

        var ok = envelope.Ok == true;
        var status = ok ? "done" : "failed";
        var error = ok ? null : string.IsNullOrWhiteSpace(envelope.Error) ? "Lệnh không thành công" : Truncate(envelope.Error, MaxError);
        var changed = await commands.TransitionAsync(
            row.Id, ["sent", "running"], status, clock.GetUtcNow(), envelope.Data?.GetRawText(), error);
        logger.LogInformation("Lệnh {CommandId} của agent {AgentId} kết thúc: {Status}", row.Id, identity.Agent.Id, changed ? status : "bỏ qua");
        if (changed && ResultObserver is { } observer)
        {
            try
            {
                var updated = await commands.FindAsync(identity.Clinic.Id, row.Id) ?? row with { Status = status };
                await observer(identity.Agent.Id, updated, envelope);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Xử lý kết quả lệnh {CommandId} thất bại", row.Id);
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Dòng lệnh nếu nó thuộc agent này, ngược lại null (chặn agent đổi trạng thái lệnh của agent khác bằng cách đoán mã).</summary>
    private async Task<AgentCommandRow?> OwnsAsync(AgentIdentity identity, Guid commandId) =>
        await commands.FindAsync(identity.Clinic.Id, commandId) is { } row && row.AgentId == identity.Agent.Id ? row : null;

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
        private volatile bool _closing;

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
                    if (_closing || Socket.State != WebSocketState.Open)
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
                    // Từ lúc này mọi lần gửi trả false (SendAsync kiểm tra cờ trong cùng khoá).
                    _closing = true;
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
