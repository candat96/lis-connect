using System.Text.Json;
using Dapper;
using Npgsql;

namespace Lis.Server.Tenancy;

/// <summary>
/// Một lệnh web gửi cho agent (bảng <c>agent_command</c> trên CSDL master). <see cref="Args"/> và <see cref="Result"/> là JSON tuỳ lệnh;
/// <see cref="Error"/> là câu tiếng Việt hiển thị cho người dùng khi lệnh không thành công.
/// </summary>
public sealed record AgentCommandRow(
    Guid Id, Guid AgentId, string Name, JsonElement? Args, string Status, string? RequestedByName, DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, JsonElement? Result, string? Error);

/// <summary>
/// Vòng đời lệnh agent trên CSDL master: tạo, chuyển trạng thái, hết hạn hàng chờ, quá hạn khi đang chạy, mất kết nối, khởi động lại server.
/// Mọi mốc thời gian là tham số <c>now</c> (hoặc mốc tính từ nó); không dùng <c>now()</c> của PostgreSQL.
/// </summary>
public sealed class AgentCommandStore(string masterConnection)
{
    private const string ExpiredError = "Agent không kết nối trong 60 giây";
    private const string TimeoutError = "Agent không trả lời";
    private const string DisconnectedError = "Agent mất kết nối giữa chừng";
    private const string RestartError = "Máy chủ khởi động lại — không rõ kết quả";

    private const string Columns = """
        id AS Id, agent_id AS AgentId, name AS Name, args::text AS ArgsJson, status AS Status,
        requested_by_name AS RequestedByName, requested_at AS RequestedAt, started_at AS StartedAt,
        finished_at AS FinishedAt, result::text AS ResultJson, error AS Error
        """;

    /// <summary>Tạo lệnh mới của một agent thuộc phòng khám; <paramref name="argsJson"/> null khi lệnh không có tham số.</summary>
    public async Task<AgentCommandRow> CreateAsync(
        Guid clinicId, Guid agentId, string name, string? argsJson, string status, Guid? userId, string? userName, DateTimeOffset now)
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleAsync<CommandRow>(
            $"""
            INSERT INTO agent_command (id, agent_id, clinic_id, name, args, status, requested_by, requested_by_name, requested_at)
            VALUES (@id, @agentId, @clinicId, @name, @argsJson::jsonb, @status, @userId, @userName, @now)
            RETURNING {Columns}
            """,
            new
            {
                id = Guid.CreateVersion7(), agentId, clinicId, name, argsJson, status, userId, userName, now = now.ToUniversalTime(),
            });
        return ToRow(row);
    }

    /// <summary>
    /// Chuyển lệnh sang <paramref name="to"/> nếu trạng thái hiện tại thuộc <paramref name="from"/>. <c>sent</c>/<c>running</c> ghi
    /// <c>started_at</c> (nếu chưa có); trạng thái kết thúc ghi <c>finished_at</c>. Không đổi được (sai trạng thái cũ) → false.
    /// </summary>
    public async Task<bool> TransitionAsync(Guid id, string[] from, string to, DateTimeOffset now, string? resultJson = null, string? error = null)
    {
        var utcNow = now.ToUniversalTime();
        var setStarted = to is "sent" or "running";
        var setFinished = to is "done" or "failed" or "expired" or "unknown";
        await using var connection = await OpenAsync();
        var changed = await connection.ExecuteAsync(
            """
            UPDATE agent_command SET
                status = @to,
                started_at = CASE WHEN @setStarted THEN coalesce(started_at, @utcNow) ELSE started_at END,
                finished_at = CASE WHEN @setFinished THEN @utcNow ELSE finished_at END,
                result = coalesce(@resultJson::jsonb, result),
                error = coalesce(@error, error)
            WHERE id = @id AND status = ANY(@fromStatuses)
            """,
            new { id, fromStatuses = from, to, setStarted, setFinished, utcNow, resultJson, error });
        return changed == 1;
    }

    /// <summary>
    /// Các lệnh <c>queued</c> của agent được xếp từ <paramref name="notBefore"/> trở đi, cũ nhất trước. Lệnh cũ hơn mốc đó đã hết hạn.
    /// </summary>
    public async Task<IReadOnlyList<AgentCommandRow>> QueuedForAsync(Guid agentId, DateTimeOffset notBefore)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<CommandRow>(
            $"""
            SELECT {Columns} FROM agent_command
            WHERE agent_id = @agentId AND status = 'queued' AND requested_at >= @notBefore
            ORDER BY requested_at
            """,
            new { agentId, notBefore = notBefore.ToUniversalTime() });
        return rows.Select(ToRow).ToList();
    }

    /// <summary>
    /// Đánh dấu <c>expired</c> mọi lệnh <c>queued</c> xếp từ <paramref name="olderThan"/> trở về trước (gọi với <c>olderThan = now − 60 giây</c>).
    /// Trả về số lệnh đã hết hạn.
    /// </summary>
    public async Task<int> ExpireQueuedAsync(DateTimeOffset olderThan, DateTimeOffset now) =>
        await ExecuteAsync(
            """
            UPDATE agent_command SET status = 'expired', finished_at = @utcNow, error = @error
            WHERE status = 'queued' AND requested_at <= @olderThan
            """,
            new { olderThan = olderThan.ToUniversalTime(), utcNow = now.ToUniversalTime(), error = ExpiredError });

    /// <summary>
    /// Lệnh <c>sent</c>/<c>running</c> quá hạn thành <c>unknown</c>: <c>update</c> 10 phút, <c>test-port</c> bằng <c>seconds</c> + 30 giây,
    /// lệnh khác 2 phút (tính từ lúc bắt đầu, hoặc lúc xếp nếu chưa bắt đầu). Trả về số lệnh đã đổi.
    /// </summary>
    public async Task<int> TimeoutRunningAsync(DateTimeOffset now) =>
        await ExecuteAsync(
            """
            UPDATE agent_command SET status = 'unknown', finished_at = @utcNow, error = @error
            WHERE status IN ('sent', 'running') AND
                  coalesce(started_at, requested_at) + CASE name
                      WHEN 'update' THEN interval '10 minutes'
                      -- seconds chỉ nhận số nguyên 0..99 (đã kẹp 60); giá trị khác (chữ, âm, rỗng, số lớn) coi như 0, không làm hỏng cả lần quét.
                      WHEN 'test-port' THEN make_interval(secs => (CASE WHEN args->>'seconds' ~ '^[0-9]{1,2}$' THEN least((args->>'seconds')::int, 60) ELSE 0 END) + 30)
                      ELSE interval '2 minutes'
                  END < @utcNow
            """,
            new { utcNow = now.ToUniversalTime(), error = TimeoutError });

    /// <summary>Lúc server khởi động: lệnh còn <c>queued</c>/<c>sent</c>/<c>running</c> thành <c>unknown</c>, không tự chạy lại.</summary>
    public async Task<int> MarkUnfinishedUnknownAsync(DateTimeOffset now) =>
        await ExecuteAsync(
            """
            UPDATE agent_command SET status = 'unknown', finished_at = @utcNow, error = @error
            WHERE status IN ('queued', 'sent', 'running')
            """,
            new { utcNow = now.ToUniversalTime(), error = RestartError });

    /// <summary>
    /// Agent mất kết nối: lệnh <c>sent</c>/<c>running</c> của agent thành <c>unknown</c>, trừ <c>update</c> (chờ agent nối lại để biết kết quả).
    /// </summary>
    public async Task<int> MarkDisconnectedAsync(Guid agentId, DateTimeOffset now) =>
        await ExecuteAsync(
            """
            UPDATE agent_command SET status = 'unknown', finished_at = @utcNow, error = @error
            WHERE agent_id = @agentId AND status IN ('sent', 'running') AND name <> 'update'
            """,
            new { agentId, utcNow = now.ToUniversalTime(), error = DisconnectedError });

    /// <summary>Một lệnh của phòng khám; lệnh của phòng khám khác hoặc không có → null.</summary>
    public async Task<AgentCommandRow?> FindAsync(Guid clinicId, Guid commandId)
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<CommandRow>(
            $"SELECT {Columns} FROM agent_command WHERE id = @commandId AND clinic_id = @clinicId",
            new { commandId, clinicId });
        return row is null ? null : ToRow(row);
    }

    /// <summary>Lịch sử lệnh của một agent thuộc phòng khám, mới nhất trước; <paramref name="limit"/> được kẹp vào khoảng 1..200.</summary>
    public async Task<IReadOnlyList<AgentCommandRow>> ListAsync(Guid clinicId, Guid agentId, int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<CommandRow>(
            $"""
            SELECT {Columns} FROM agent_command
            WHERE clinic_id = @clinicId AND agent_id = @agentId
            ORDER BY requested_at DESC
            LIMIT @limit
            """,
            new { clinicId, agentId, limit });
        return rows.Select(ToRow).ToList();
    }

    /// <summary>Lệnh <c>update</c> mới nhất của agent đang <c>sent</c>/<c>running</c>/<c>unknown</c> và được xếp trong 30 phút qua; không có → null.</summary>
    public async Task<AgentCommandRow?> OpenUpdateAsync(Guid agentId, DateTimeOffset now)
    {
        await using var connection = await OpenAsync();
        var row = await connection.QueryFirstOrDefaultAsync<CommandRow>(
            $"""
            SELECT {Columns} FROM agent_command
            WHERE agent_id = @agentId AND name = 'update' AND status IN ('sent', 'running', 'unknown') AND requested_at >= @since
            ORDER BY requested_at DESC
            LIMIT 1
            """,
            new { agentId, since = now.AddMinutes(-30).ToUniversalTime() });
        return row is null ? null : ToRow(row);
    }

    private async Task<int> ExecuteAsync(string sql, object parameters)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(masterConnection);
        await connection.OpenAsync();
        return connection;
    }

    private static AgentCommandRow ToRow(CommandRow row) => new(
        row.Id, row.AgentId, row.Name, ParseJson(row.ArgsJson), row.Status, row.RequestedByName, row.RequestedAt,
        row.StartedAt, row.FinishedAt, ParseJson(row.ResultJson), row.Error);

    private static JsonElement? ParseJson(string? json) => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);

    private sealed record CommandRow
    {
        public Guid Id { get; init; }
        public Guid AgentId { get; init; }
        public string Name { get; init; } = "";
        public string? ArgsJson { get; init; }
        public string Status { get; init; } = "";
        public string? RequestedByName { get; init; }
        public DateTimeOffset RequestedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string? ResultJson { get; init; }
        public string? Error { get; init; }
    }
}
