using Dapper;
using Lis.Server.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>
/// CSDL master chạy bằng migration đánh số: tạo mới đủ schema, nâng được CSDL cũ (tạo trước khi có migration) mà không mất dữ liệu,
/// và chạy lại không áp thêm gì.
/// </summary>
public sealed class MasterMigrationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    /// <summary>Nguyên văn khối schema mà <c>EnsureSchemaAsync</c> tạo trước khi chuyển sang migration đánh số.</summary>
    private const string LegacySchema = """
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
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewDbName() => $"lis_master_mig_{Guid.NewGuid():N}";

    private string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(fixture.MasterConnection) { Database = database }.ConnectionString;

    [Fact]
    public async Task Master_trang_chay_du_migration_va_co_bang_agent_pairing()
    {
        var db = NewDbName();

        await new MasterStore(ConnectionTo(db)).EnsureSchemaAsync();

        await using var connection = new NpgsqlConnection(ConnectionTo(db));
        await connection.OpenAsync(Ct);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM schemaversions")).ShouldBe(4);
        (await connection.ExecuteScalarAsync<string>("SELECT scriptname FROM schemaversions ORDER BY scriptname DESC LIMIT 1"))
            .ShouldEndWith("0004_giu_lich_su_lenh.sql");

        var agentColumns = (await connection.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'agent'")).ToList();
        agentColumns.ShouldContain("paired_at");
        agentColumns.ShouldContain("machine_name");
        agentColumns.ShouldContain("agent_version");

        (await connection.ExecuteScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'agent' AND column_name = 'key_hash'"))
            .ShouldBe("YES");

        (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('public.agent_pairing') IS NOT NULL")).ShouldBeTrue();
        (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('public.agent_command') IS NOT NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task Master_cu_tao_bang_CREATE_IF_NOT_EXISTS_nang_cap_duoc_va_giu_du_lieu()
    {
        var db = NewDbName();
        await using (var admin = new NpgsqlConnection(ConnectionTo("postgres")))
        {
            await admin.OpenAsync(Ct);
            await admin.ExecuteAsync($"CREATE DATABASE \"{db}\"");
        }

        var clinicId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        await using (var legacy = new NpgsqlConnection(ConnectionTo(db)))
        {
            await legacy.OpenAsync(Ct);
            await legacy.ExecuteAsync(LegacySchema);
            await legacy.ExecuteAsync(
                "INSERT INTO clinic (id, code, name, db_name) VALUES (@clinicId, 'thu-cu', 'Phòng khám Thử cũ', 'clinic_thu_cu')",
                new { clinicId });
            await legacy.ExecuteAsync(
                "INSERT INTO agent (id, clinic_id, name, workstation_code, key_hash) VALUES (@agentId, @clinicId, 'Máy Thử cũ', 'WS-CU', 'hash-thu')",
                new { agentId, clinicId });
        }

        await new MasterStore(ConnectionTo(db)).EnsureSchemaAsync();

        await using var connection = new NpgsqlConnection(ConnectionTo(db));
        await connection.OpenAsync(Ct);
        (await connection.ExecuteScalarAsync<string>("SELECT name FROM clinic WHERE id = @clinicId", new { clinicId })).ShouldBe("Phòng khám Thử cũ");
        (await connection.ExecuteScalarAsync<string>("SELECT key_hash FROM agent WHERE id = @agentId", new { agentId })).ShouldBe("hash-thu");
        (await connection.ExecuteScalarAsync<bool>(
            "SELECT paired_at IS NOT NULL FROM agent WHERE id = @agentId", new { agentId })).ShouldBeTrue();
    }

    [Fact]
    public async Task Chay_hai_lan_khong_ap_them_gi()
    {
        var db = NewDbName();
        await new MasterStore(ConnectionTo(db)).EnsureSchemaAsync();

        var again = MasterMigrator.Upgrade(ConnectionTo(db));

        again.Ok.ShouldBeTrue();
        again.Applied.ShouldBeEmpty();
    }
}
