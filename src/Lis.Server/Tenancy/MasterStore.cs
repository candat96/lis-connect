using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Lis.Server.Tenancy;

/// <summary>Một phòng khám trên server: mã (dùng trong URL/đăng nhập), tên, và tên CSDL riêng của nó.</summary>
public sealed record Clinic(Guid Id, string Code, string Name, string DbName);

/// <summary>Một agent (máy tính cạnh máy xét nghiệm) của một phòng khám. <see cref="WorkstationCode"/> khớp <c>instrument.workstation_code</c>.</summary>
public sealed record AgentInfo(Guid Id, Guid ClinicId, string Name, string WorkstationCode, DateTimeOffset? LastSeenAt);

/// <summary>Agent đã xác thực kèm phòng khám của nó.</summary>
public sealed record AgentIdentity(AgentInfo Agent, Clinic Clinic);

/// <summary>
/// CSDL "master" của server: danh sách phòng khám và agent. Dữ liệu nghiệp vụ của mỗi phòng khám
/// nằm ở CSDL riêng (<see cref="Clinic.DbName"/>) — cách ly hẳn, sao lưu/chuyển từng phòng khám độc lập.
/// </summary>
public sealed class MasterStore(string connectionString)
{
    private const string KeyPrefix = "lisa";

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

    /// <summary>Tạo agent và trả về khoá — CHỈ hiện một lần, server chỉ lưu bản băm.</summary>
    public async Task<(AgentInfo Agent, string Key)> AddAgentAsync(Guid clinicId, string name, string workstationCode)
    {
        var id = Guid.CreateVersion7();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO agent (id, clinic_id, name, workstation_code, key_hash) VALUES (@id, @clinicId, @name, @workstationCode, @hash)",
            new { id, clinicId, name = name.Trim(), workstationCode = workstationCode.Trim(), hash = Hash(secret) });
        return (new AgentInfo(id, clinicId, name.Trim(), workstationCode.Trim(), null), $"{KeyPrefix}_{id:N}_{secret}");
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
        public string ClinicCode { get; init; } = "";
        public string ClinicName { get; init; } = "";
        public string DbName { get; init; } = "";
    }
}
