using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Lis.Server.Tenancy;

/// <summary>Một phòng khám trên server: mã (dùng trong URL/đăng nhập), tên, và tên CSDL riêng của nó.</summary>
public sealed record Clinic(Guid Id, string Code, string Name, string DbName);

/// <summary>Một agent (máy tính cạnh máy xét nghiệm) của một phòng khám. <see cref="WorkstationCode"/> khớp <c>instrument.workstation_code</c>.</summary>
public sealed record AgentInfo(Guid Id, Guid ClinicId, string Name, string WorkstationCode, DateTimeOffset? LastSeenAt);

/// <summary>Agent kèm trạng thái các máy nó báo về lần cuối (JSON mảng <c>InstrumentStatusReport</c>; null khi chưa báo).</summary>
public sealed record AgentWithStatus(AgentInfo Agent, string? LastStatusJson);

/// <summary>Agent đã xác thực kèm phòng khám của nó.</summary>
public sealed record AgentIdentity(AgentInfo Agent, Clinic Clinic);

/// <summary>Lý do một thao tác sửa / tạo agent thất bại.</summary>
public enum AgentFailure
{
    /// <summary>Không có agent này trong phòng khám.</summary>
    NotFound,

    /// <summary>Mã máy trạm đã có agent khác của phòng khám dùng (ràng buộc duy nhất trong CSDL).</summary>
    DuplicateWorkstation,
}

/// <summary>Lỗi nghiệp vụ của agent; <see cref="Exception.Message"/> là câu tiếng Việt hiển thị cho người dùng.</summary>
public sealed class AgentException(AgentFailure failure, string message) : InvalidOperationException(message)
{
    public AgentFailure Failure { get; } = failure;
}

/// <summary>
/// CSDL "master" của server: danh sách phòng khám và agent. Dữ liệu nghiệp vụ của mỗi phòng khám
/// nằm ở CSDL riêng (<see cref="Clinic.DbName"/>) — cách ly hẳn, sao lưu/chuyển từng phòng khám độc lập.
/// </summary>
public sealed class MasterStore(string connectionString)
{
    private const string KeyPrefix = "lisa";

    /// <summary>Tên CSDL master đọc từ chuỗi kết nối — không phòng khám nào được dùng tên này làm CSDL của mình.</summary>
    public string DatabaseName { get; } = new NpgsqlConnectionStringBuilder(connectionString).Database
        ?? throw new InvalidOperationException("Chuỗi kết nối master thiếu tên CSDL (Database=)");

    public async Task EnsureSchemaAsync()
    {
        // CSDL master chưa có thì tạo (lần chạy đầu trên máy chủ mới).
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var adminConnection = new NpgsqlConnection(admin.ConnectionString))
        {
            await adminConnection.OpenAsync();
            var exists = await adminConnection.ExecuteScalarAsync<int?>("SELECT 1 FROM pg_database WHERE datname = @name", new { name = target.Database });
            if (exists is null)
            {
                await adminConnection.ExecuteAsync($"CREATE DATABASE \"{target.Database}\"");
            }
        }

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS clinic (
                id uuid PRIMARY KEY,
                code text NOT NULL UNIQUE,
                name text NOT NULL,
                db_name text NOT NULL UNIQUE,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS agent (
                id uuid PRIMARY KEY,
                clinic_id uuid NOT NULL REFERENCES clinic (id),
                name text NOT NULL,
                workstation_code text NOT NULL,
                key_hash text NOT NULL,
                last_seen_at timestamptz NULL,
                last_status jsonb NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE (clinic_id, workstation_code)
            );
            -- Chống ghi trùng khi agent gửi lại một bản tin (mất mạng lúc chờ phản hồi).
            CREATE TABLE IF NOT EXISTS agent_reading (
                clinic_id uuid NOT NULL REFERENCES clinic (id),
                reading_key text NOT NULL,
                outcome jsonb NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (clinic_id, reading_key)
            );
            """);
    }

    public async Task<Clinic> AddClinicAsync(string code, string name, string dbName)
    {
        var clinic = new Clinic(Guid.CreateVersion7(), code.Trim().ToLowerInvariant(), name.Trim(), dbName.Trim());
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO clinic (id, code, name, db_name) VALUES (@Id, @Code, @Name, @DbName)", clinic);
        return clinic;
    }

    public async Task<IReadOnlyList<Clinic>> ClinicsAsync()
    {
        await using var connection = await OpenAsync();
        return (await connection.QueryAsync<Clinic>("SELECT id, code, name, db_name AS DbName FROM clinic ORDER BY code")).ToList();
    }

    public async Task<Clinic?> FindClinicAsync(string code)
    {
        await using var connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<Clinic>(
            "SELECT id, code, name, db_name AS DbName FROM clinic WHERE code = @code", new { code = code.Trim().ToLowerInvariant() });
    }

    public async Task<Clinic?> FindClinicByIdAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<Clinic>(
            "SELECT id, code, name, db_name AS DbName FROM clinic WHERE id = @id", new { id });
    }

    /// <summary>Mọi agent của một phòng khám (theo tên) kèm trạng thái máy báo về lần cuối.</summary>
    public async Task<IReadOnlyList<AgentWithStatus>> AgentsOfAsync(Guid clinicId)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<AgentRow>(
            """
            SELECT id, clinic_id AS ClinicId, name, workstation_code AS WorkstationCode, last_seen_at AS LastSeenAt,
                   last_status::text AS LastStatus
            FROM agent WHERE clinic_id = @clinicId ORDER BY name
            """,
            new { clinicId });
        return rows.Select(r => new AgentWithStatus(new AgentInfo(r.Id, r.ClinicId, r.Name, r.WorkstationCode, r.LastSeenAt), r.LastStatus)).ToList();
    }

    /// <summary>
    /// Tạo agent và trả về khoá — CHỈ hiện một lần, server chỉ lưu bản băm.
    /// Mã máy trạm trùng agent khác của phòng khám → <see cref="AgentException"/> (<see cref="AgentFailure.DuplicateWorkstation"/>).
    /// </summary>
    public async Task<(AgentInfo Agent, string Key)> AddAgentAsync(Guid clinicId, string name, string workstationCode)
    {
        var id = Guid.CreateVersion7();
        var (key, hash) = NewKey(id);
        await using var connection = await OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO agent (id, clinic_id, name, workstation_code, key_hash) VALUES (@id, @clinicId, @name, @workstationCode, @hash)",
                new { id, clinicId, name = name.Trim(), workstationCode = workstationCode.Trim(), hash });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DuplicateWorkstation(workstationCode);
        }

        return (new AgentInfo(id, clinicId, name.Trim(), workstationCode.Trim(), null), key);
    }

    /// <summary>
    /// Sửa tên và mã máy trạm của agent thuộc <paramref name="clinicId"/>; trả về mã máy trạm CŨ.
    /// Không có (hoặc của phòng khám khác) → <see cref="AgentFailure.NotFound"/>; trùng mã → <see cref="AgentFailure.DuplicateWorkstation"/>.
    /// </summary>
    public async Task<string> UpdateAgentAsync(Guid clinicId, Guid agentId, string name, string workstationCode)
    {
        await using var connection = await OpenAsync();
        try
        {
            var previous = await connection.ExecuteScalarAsync<string?>(
                """
                WITH old AS (SELECT workstation_code FROM agent WHERE id = @agentId AND clinic_id = @clinicId)
                UPDATE agent SET name = @name, workstation_code = @workstationCode
                WHERE id = @agentId AND clinic_id = @clinicId
                RETURNING (SELECT workstation_code FROM old)
                """,
                new { agentId, clinicId, name = name.Trim(), workstationCode = workstationCode.Trim() });
            return previous ?? throw NotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DuplicateWorkstation(workstationCode);
        }
    }

    /// <summary>Cấp khoá mới cho agent (khoá cũ hết hiệu lực ngay) và trả về khoá — CHỈ hiện một lần. Không có → <see cref="AgentFailure.NotFound"/>.</summary>
    public async Task<string> RegenerateAgentKeyAsync(Guid clinicId, Guid agentId)
    {
        var (key, hash) = NewKey(agentId);
        await using var connection = await OpenAsync();
        var updated = await connection.ExecuteAsync(
            "UPDATE agent SET key_hash = @hash WHERE id = @agentId AND clinic_id = @clinicId", new { hash, agentId, clinicId });
        return updated == 1 ? key : throw NotFound();
    }

    /// <summary>Xoá agent thuộc <paramref name="clinicId"/>; false khi không có.</summary>
    public async Task<bool> DeleteAgentAsync(Guid clinicId, Guid agentId)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM agent WHERE id = @agentId AND clinic_id = @clinicId", new { agentId, clinicId }) == 1;
    }

    private static AgentException NotFound() => new(AgentFailure.NotFound, "Không tìm thấy agent");

    private static AgentException DuplicateWorkstation(string workstationCode) =>
        new(AgentFailure.DuplicateWorkstation, $"Mã máy trạm {workstationCode.Trim()} đã có agent khác dùng");

    /// <summary>Khoá <c>lisa_&lt;id không gạch&gt;_&lt;bí mật hex&gt;</c> và bản băm của bí mật để lưu.</summary>
    private static (string Key, string Hash) NewKey(Guid agentId)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return ($"{KeyPrefix}_{agentId:N}_{secret}", Hash(secret));
    }

    /// <summary>Xác thực khoá agent (<c>lisa_&lt;id&gt;_&lt;bí mật&gt;</c>). Sai định dạng / không có / sai bí mật → null.</summary>
    public async Task<AgentIdentity?> AuthenticateAsync(string? key)
    {
        var parts = (key ?? "").Split('_');
        if (parts.Length != 3 || parts[0] != KeyPrefix || !Guid.TryParseExact(parts[1], "N", out var agentId))
        {
            return null;
        }

        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<AgentRow>(
            """
            SELECT a.id, a.clinic_id AS ClinicId, a.name, a.workstation_code AS WorkstationCode, a.key_hash AS KeyHash,
                   a.last_seen_at AS LastSeenAt, c.code AS ClinicCode, c.name AS ClinicName, c.db_name AS DbName
            FROM agent a JOIN clinic c ON c.id = a.clinic_id
            WHERE a.id = @agentId
            """,
            new { agentId });
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.KeyHash), Encoding.ASCII.GetBytes(Hash(parts[2]))))
        {
            return null;
        }

        return new AgentIdentity(
            new AgentInfo(row.Id, row.ClinicId, row.Name, row.WorkstationCode, row.LastSeenAt),
            new Clinic(row.ClinicId, row.ClinicCode, row.ClinicName, row.DbName));
    }

    public async Task TouchAgentAsync(Guid agentId, string? statusJson)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE agent SET last_seen_at = now(), last_status = coalesce(@statusJson::jsonb, last_status) WHERE id = @agentId",
            new { agentId, statusJson });
    }

    /// <summary>
    /// Giữ chỗ cho một bản tin trước khi ghi. Trả về true nếu bản tin này chưa từng được nhận
    /// (hoặc lần giữ chỗ trước bị bỏ dở quá 2 phút — server chết giữa chừng); false khi agent gửi
    /// lại: kèm kết quả lần trước nếu đã ghi xong, null nếu một request khác đang ghi dở.
    /// </summary>
    public async Task<(bool IsNew, string? PreviousOutcome)> ReserveReadingAsync(Guid clinicId, string readingKey)
    {
        await using var connection = await OpenAsync();
        var inserted = await connection.ExecuteAsync(
            """
            INSERT INTO agent_reading (clinic_id, reading_key) VALUES (@clinicId, @readingKey)
            ON CONFLICT (clinic_id, reading_key) DO UPDATE SET created_at = now()
            WHERE agent_reading.outcome IS NULL AND agent_reading.created_at < now() - interval '2 minutes'
            """,
            new { clinicId, readingKey });
        if (inserted == 1)
        {
            return (true, null);
        }

        var previous = await connection.ExecuteScalarAsync<string?>(
            "SELECT outcome::text FROM agent_reading WHERE clinic_id = @clinicId AND reading_key = @readingKey", new { clinicId, readingKey });
        return (false, previous);
    }

    public async Task CompleteReadingAsync(Guid clinicId, string readingKey, string outcomeJson)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE agent_reading SET outcome = @outcomeJson::jsonb WHERE clinic_id = @clinicId AND reading_key = @readingKey",
            new { clinicId, readingKey, outcomeJson });
    }

    /// <summary>Ghi thất bại → bỏ giữ chỗ để lần gửi lại được xử lý như mới.</summary>
    public async Task ReleaseReadingAsync(Guid clinicId, string readingKey)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM agent_reading WHERE clinic_id = @clinicId AND reading_key = @readingKey AND outcome IS NULL", new { clinicId, readingKey });
    }

    /// <summary>
    /// Khoá <c>pg_advisory_lock</c> trên CSDL master, giữ đến khi huỷ kết quả trả về: mọi tiến trình dùng chung master
    /// (nhiều request, nhiều bản server) xếp hàng qua cùng một khoá.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireLockAsync(long key)
    {
        var connection = await OpenAsync();
        try
        {
            await connection.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key });
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new AdvisoryLock(connection, key);
    }

    private sealed class AdvisoryLock(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key });
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private sealed record AgentRow
    {
        public Guid Id { get; init; }
        public Guid ClinicId { get; init; }
        public string Name { get; init; } = "";
        public string WorkstationCode { get; init; } = "";
        public string KeyHash { get; init; } = "";
        public DateTimeOffset? LastSeenAt { get; init; }
        public string? LastStatus { get; init; }
        public string ClinicCode { get; init; } = "";
        public string ClinicName { get; init; } = "";
        public string DbName { get; init; } = "";
    }
}
