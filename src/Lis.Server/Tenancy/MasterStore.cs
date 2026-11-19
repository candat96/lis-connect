using System.Security.Cryptography;
using System.Text;
using Dapper;
using Lis.Core.Agents;
using Npgsql;

namespace Lis.Server.Tenancy;

/// <summary>Một phòng khám trên server: mã (dùng trong URL/đăng nhập), tên, và tên CSDL riêng của nó.</summary>
public sealed record Clinic(Guid Id, string Code, string Name, string DbName);

/// <summary>Một agent (máy tính cạnh máy xét nghiệm) của một phòng khám. <see cref="WorkstationCode"/> khớp <c>instrument.workstation_code</c>.</summary>
public sealed record AgentInfo(Guid Id, Guid ClinicId, string Name, string WorkstationCode, DateTimeOffset? LastSeenAt);

/// <summary>Agent kèm trạng thái các máy nó báo về lần cuối (JSON mảng <c>InstrumentStatusReport</c>; null khi chưa báo).</summary>
public sealed record AgentWithStatus(
    AgentInfo Agent, string? LastStatusJson, DateTimeOffset? PairedAt = null, string? MachineName = null, string? AgentVersion = null,
    DateTimeOffset? PairingExpiresAt = null, string? LastPortsJson = null, DateTimeOffset? LastPortsAt = null);

/// <summary>Mã ghép nối vừa tạo: <see cref="Secret"/> chỉ có lúc này (server chỉ lưu bản băm); hết hạn lúc <see cref="ExpiresAt"/>.</summary>
public sealed record PairingTicket(string Secret, DateTimeOffset ExpiresAt);

/// <summary>Vì sao một mã ghép nối không đổi được: không có mã này / đã dùng / quá hạn.</summary>
public enum PairingFailure
{
    Unknown,
    Used,
    Expired,
}

/// <summary>Kết quả đổi mã: thành công thì có <see cref="Identity"/> + <see cref="AgentKey"/> (khoá mới, chỉ hiện một lần), ngược lại có <see cref="Failure"/>.</summary>
public sealed record PairingRedemption(AgentIdentity? Identity, string? AgentKey, PairingFailure? Failure);

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

    /// <summary>Mã ghép nối chỉ dùng được trong khoảng này kể từ lúc tạo.</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(30);

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

        var migration = MasterMigrator.Upgrade(connectionString);
        if (!migration.Ok)
        {
            throw new InvalidOperationException($"Nâng cấp CSDL lis_master lỗi: {migration.Error ?? "lỗi không rõ"}");
        }
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
    public async Task<IReadOnlyList<AgentWithStatus>> AgentsOfAsync(Guid clinicId, DateTimeOffset now)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<AgentRow>(
            """
            SELECT a.id, a.clinic_id AS ClinicId, a.name, a.workstation_code AS WorkstationCode, a.last_seen_at AS LastSeenAt,
                   a.last_status::text AS LastStatus, a.paired_at AS PairedAt, a.machine_name AS MachineName, a.agent_version AS AgentVersion,
                   (SELECT max(p.expires_at) FROM agent_pairing p
                    WHERE p.agent_id = a.id AND p.used_at IS NULL AND p.expires_at > @now) AS PairingExpiresAt,
                   a.last_ports::text AS LastPortsJson, a.last_ports_at AS LastPortsAt
            FROM agent a WHERE a.clinic_id = @clinicId ORDER BY a.name
            """,
            new { clinicId, now = now.ToUniversalTime() });
        return rows.Select(r => new AgentWithStatus(
            new AgentInfo(r.Id, r.ClinicId, r.Name, r.WorkstationCode, r.LastSeenAt), r.LastStatus,
            r.PairedAt, r.MachineName, r.AgentVersion, r.PairingExpiresAt, r.LastPortsJson, r.LastPortsAt)).ToList();
    }

    /// <summary>
    /// Tạo agent chưa có khoá (mã máy trạm tự sinh = id không gạch) kèm mã ghép nối đầu tiên, trong một giao dịch.
    /// </summary>
    public async Task<(AgentInfo Agent, PairingTicket Pairing)> CreateAgentAsync(Guid clinicId, string name, Guid? createdBy, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        var workstationCode = id.ToString("N");
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO agent (id, clinic_id, name, workstation_code, key_hash) VALUES (@id, @clinicId, @name, @workstationCode, NULL)",
            new { id, clinicId, name = name.Trim(), workstationCode }, tx);
        var ticket = await InsertPairingAsync(connection, tx, id, createdBy, now);
        await tx.CommitAsync();
        return (new AgentInfo(id, clinicId, name.Trim(), workstationCode, null), ticket);
    }

    /// <summary>
    /// Cấp mã ghép nối mới cho agent: mã chưa dùng trước đó bị xoá (mỗi agent tối đa một mã còn hiệu lực).
    /// Khoá hiện tại của agent (nếu có) vẫn chạy cho tới khi mã mới được đổi. Agent không thuộc phòng khám → <see cref="AgentFailure.NotFound"/>.
    /// </summary>
    public async Task<PairingTicket> CreatePairingAsync(Guid clinicId, Guid agentId, Guid? createdBy, DateTimeOffset now)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        // Khoá dòng agent để hai yêu cầu cùng lúc không để lại hai mã còn hiệu lực.
        var exists = await connection.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM agent WHERE id = @agentId AND clinic_id = @clinicId FOR UPDATE", new { agentId, clinicId }, tx);
        if (exists is null)
        {
            throw NotFound();
        }

        var ticket = await InsertPairingAsync(connection, tx, agentId, createdBy, now);
        await tx.CommitAsync();
        return ticket;
    }

    private static async Task<PairingTicket> InsertPairingAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid agentId, Guid? createdBy, DateTimeOffset now)
    {
        var secret = PairingCode.NewSecret();
        var expiresAt = (now + PairingLifetime).ToUniversalTime();
        await connection.ExecuteAsync("DELETE FROM agent_pairing WHERE agent_id = @agentId AND used_at IS NULL", new { agentId }, tx);
        await connection.ExecuteAsync(
            "INSERT INTO agent_pairing (id, agent_id, secret_hash, expires_at, created_by, created_at) VALUES (@id, @agentId, @hash, @expiresAt, @createdBy, @now)",
            new { id = Guid.CreateVersion7(), agentId, hash = Hash(secret), expiresAt, createdBy, now = now.ToUniversalTime() }, tx);
        return new PairingTicket(secret, expiresAt);
    }

    /// <summary>Phòng khám của mã ghép nối (dù đã dùng hay hết hạn); null nếu không có mã này. Không tiêu mã.</summary>
    public async Task<Clinic?> FindPairingClinicAsync(string secret)
    {
        await using var connection = await OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<Clinic>(
            """
            SELECT c.id, c.code, c.name, c.db_name AS DbName
            FROM agent_pairing p JOIN agent a ON a.id = p.agent_id JOIN clinic c ON c.id = a.clinic_id
            WHERE p.secret_hash = @hash
            """,
            new { hash = Hash(secret) });
    }

    /// <summary>
    /// Đổi mã ghép nối lấy khoá agent mới, trong một giao dịch. Việc đánh dấu "đã dùng" là một câu
    /// <c>UPDATE … RETURNING</c> nguyên tử nên hai yêu cầu cùng mã cùng lúc chỉ một bên thắng.
    /// Khoá cũ của agent (nếu có) hết hiệu lực ngay khi đổi xong.
    /// </summary>
    public async Task<PairingRedemption> RedeemPairingAsync(string secret, string? machineName, string? agentVersion, DateTimeOffset now)
    {
        var hash = Hash(secret);
        var utcNow = now.ToUniversalTime();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var agentId = await connection.ExecuteScalarAsync<Guid?>(
            "UPDATE agent_pairing SET used_at = @utcNow WHERE secret_hash = @hash AND used_at IS NULL AND expires_at > @utcNow RETURNING agent_id",
            new { hash, utcNow }, tx);
        if (agentId is null)
        {
            // Đã dùng thì báo "đã dùng" kể cả khi cũng đã quá hạn.
            var used = await connection.ExecuteScalarAsync<bool?>(
                "SELECT used_at IS NOT NULL FROM agent_pairing WHERE secret_hash = @hash", new { hash }, tx);
            await tx.RollbackAsync();
            var failure = used is null ? PairingFailure.Unknown : used.Value ? PairingFailure.Used : PairingFailure.Expired;
            return new PairingRedemption(null, null, failure);
        }

        var (key, keyHash) = NewKey(agentId.Value);
        var row = await connection.QuerySingleOrDefaultAsync<AgentRow>(
            """
            WITH upd AS (
                UPDATE agent SET key_hash = @keyHash, paired_at = @utcNow, machine_name = @machineName, agent_version = @agentVersion
                WHERE id = @agentId
                RETURNING id, clinic_id, name, workstation_code, last_seen_at
            )
            SELECT u.id, u.clinic_id AS ClinicId, u.name, u.workstation_code AS WorkstationCode, u.last_seen_at AS LastSeenAt,
                   c.code AS ClinicCode, c.name AS ClinicName, c.db_name AS DbName
            FROM upd u JOIN clinic c ON c.id = u.clinic_id
            """,
            new { agentId, keyHash, utcNow, machineName, agentVersion }, tx);
        if (row is null)
        {
            await tx.RollbackAsync();
            return new PairingRedemption(null, null, PairingFailure.Unknown);
        }

        await tx.CommitAsync();
        return new PairingRedemption(ToIdentity(row), key, null);
    }

    private static AgentIdentity ToIdentity(AgentRow row) => new(
        new AgentInfo(row.Id, row.ClinicId, row.Name, row.WorkstationCode, row.LastSeenAt),
        new Clinic(row.ClinicId, row.ClinicCode, row.ClinicName, row.DbName));

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
                "INSERT INTO agent (id, clinic_id, name, workstation_code, key_hash, paired_at) VALUES (@id, @clinicId, @name, @workstationCode, @hash, now())",
                new { id, clinicId, name = name.Trim(), workstationCode = workstationCode.Trim(), hash });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw DuplicateWorkstation(workstationCode);
        }

        return (new AgentInfo(id, clinicId, name.Trim(), workstationCode.Trim(), null), key);
    }

    /// <summary>Đổi tên agent thuộc <paramref name="clinicId"/>; mã máy trạm giữ nguyên. Không có (hoặc của phòng khám khác) → <see cref="AgentFailure.NotFound"/>.</summary>
    public async Task RenameAgentAsync(Guid clinicId, Guid agentId, string name)
    {
        await using var connection = await OpenAsync();
        var updated = await connection.ExecuteAsync(
            "UPDATE agent SET name = @name WHERE id = @agentId AND clinic_id = @clinicId", new { name = name.Trim(), agentId, clinicId });
        if (updated == 0) throw NotFound();
    }

    /// <summary>Xoá agent thuộc <paramref name="clinicId"/>; false khi không có.</summary>
    public async Task<bool> DeleteAgentAsync(Guid clinicId, Guid agentId)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteAsync("DELETE FROM agent WHERE id = @agentId AND clinic_id = @clinicId", new { agentId, clinicId }) == 1;
    }

    /// <summary>
    /// Thu hồi khoá agent: khoá cũ hết hiệu lực ngay (kể cả khi agent đang tắt), mã ghép nối chưa dùng bị huỷ.
    /// Không có agent này trong phòng khám → false.
    /// </summary>
    public async Task<bool> RevokeAgentAsync(Guid clinicId, Guid agentId)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var updated = await connection.ExecuteAsync(
            "UPDATE agent SET key_hash = NULL, paired_at = NULL WHERE id = @agentId AND clinic_id = @clinicId",
            new { agentId, clinicId }, tx);
        if (updated == 0)
        {
            await tx.RollbackAsync();
            return false;
        }

        await connection.ExecuteAsync("DELETE FROM agent_pairing WHERE agent_id = @agentId AND used_at IS NULL", new { agentId }, tx);
        await tx.CommitAsync();
        return true;
    }

    /// <summary>Lưu danh sách cổng COM agent quét được lần cuối (JSON), mốc <paramref name="now"/>.</summary>
    public async Task SavePortsAsync(Guid agentId, string portsJson, DateTimeOffset now)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE agent SET last_ports = @portsJson::jsonb, last_ports_at = @now WHERE id = @agentId",
            new { agentId, portsJson, now = now.ToUniversalTime() });
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
        // Agent mới tạo chưa đổi mã ghép nối thì chưa có khoá — không khoá nào được chấp nhận.
        if (row?.KeyHash is not { } storedHash
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(parts[2]))))
        {
            return null;
        }

        return ToIdentity(row);
    }

    /// <summary>Agent báo "còn sống"; cột nào truyền null thì giữ giá trị cũ.</summary>
    public async Task TouchAgentAsync(Guid agentId, string? statusJson, string? machineName = null, string? agentVersion = null)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE agent SET last_seen_at = now(), last_status = coalesce(@statusJson::jsonb, last_status),
                             machine_name = coalesce(@machineName, machine_name), agent_version = coalesce(@agentVersion, agent_version)
            WHERE id = @agentId
            """,
            new { agentId, statusJson, machineName, agentVersion });
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
        public string? KeyHash { get; init; }
        public DateTimeOffset? PairedAt { get; init; }
        public string? MachineName { get; init; }
        public string? AgentVersion { get; init; }
        public DateTimeOffset? PairingExpiresAt { get; init; }
        public string? LastPortsJson { get; init; }
        public DateTimeOffset? LastPortsAt { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public string? LastStatus { get; init; }
        public string ClinicCode { get; init; } = "";
        public string ClinicName { get; init; } = "";
        public string DbName { get; init; } = "";
    }
}
