using Dapper;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lis.Data.Tests;

/// <summary>
/// Các lớp test dùng chung một container PostgreSQL (một container cho cả lần chạy test);
/// mỗi lớp vẫn lấy CSDL riêng của mình qua <see cref="PostgresFixture.NewDatabaseAsync"/>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// Fixture cho PostgreSQL 16 (kiểm tra tương thích).
/// </summary>
public sealed class Postgres16Fixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Tạo một CSDL mới (tên ngẫu nhiên) trong container đang chạy, migrate lên bản mới nhất,
    /// trả về chuỗi kết nối tới CSDL đó.
    /// </summary>
    public async Task<string> NewDatabaseAsync()
    {
        var connectionString = await NewEmptyDatabaseAsync();

        var result = Migrator.Upgrade(connectionString);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"Migration CSDL kiểm thử thất bại: {result.Error}");
        }

        return connectionString;
    }

    /// <summary>
    /// Tạo một CSDL mới (tên ngẫu nhiên), còn trống, chưa chạy migration.
    /// </summary>
    public async Task<string> NewEmptyDatabaseAsync()
    {
        if (_container is null)
        {
            throw new InvalidOperationException("Container PostgreSQL chưa khởi động.");
        }

        var databaseName = $"lis_test_{Guid.NewGuid():N}";
        var adminConnectionString = _container.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        return connectionStringBuilder.ConnectionString;
    }
}

/// <summary>
/// Collection cho PostgreSQL 16 tests.
/// </summary>
[CollectionDefinition("Postgres16")]
public sealed class Postgres16Collection : ICollectionFixture<Postgres16Fixture>
{
    public const string Name = "Postgres16";
}

/// <summary>
/// Kiểm tra migration (DbUp) và một số ràng buộc quan trọng của lược đồ v1.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigratorTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Upgrade_EmptyDb_AppliesAllAndIsIdempotent()
    {
        var connectionString = await fixture.NewEmptyDatabaseAsync();

        var first = Migrator.Upgrade(connectionString);
        first.Ok.ShouldBeTrue(first.Error);
        first.Applied.ShouldNotBeEmpty();

        var second = Migrator.Upgrade(connectionString);
        second.Ok.ShouldBeTrue(second.Error);
        second.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task IsEmpty_TrueBeforeFalseAfter()
    {
        var connectionString = await fixture.NewEmptyDatabaseAsync();

        Migrator.IsEmpty(connectionString).ShouldBeTrue();

        var result = Migrator.Upgrade(connectionString);
        result.Ok.ShouldBeTrue(result.Error);

        Migrator.IsEmpty(connectionString).ShouldBeFalse();
    }

    [Fact]
    public async Task ChannelMap_DuplicateChannel_Rejected()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);

        var instrumentId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO instrument (name) VALUES ('Máy thử') RETURNING id");
        var serviceId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO service (code, name, value_type) VALUES ('GLU-THU', 'Glucose (thử)', 'so') RETURNING id");

        await connection.ExecuteAsync(
            "INSERT INTO instrument_channel (instrument_id, channel_code, service_id) VALUES (@instrumentId, @channelCode, @serviceId)",
            new { instrumentId, channelCode = "CH1", serviceId });

        var exception = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(
            "INSERT INTO instrument_channel (instrument_id, channel_code, service_id) VALUES (@instrumentId, @channelCode, @serviceId)",
            new { instrumentId, channelCode = "CH1", serviceId }));

        exception.SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task Sample_NonDigitBarcode_Rejected()
    {
        var connectionString = await fixture.NewDatabaseAsync();
        var db = Db.Create(connectionString);
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);

        var patientId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO patient (code, full_name, sex) VALUES ('BN-THU-001', 'Nguyễn Văn Thử', 'M') RETURNING id");
        var orderId = await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO lab_order (patient_id) VALUES (@patientId) RETURNING id",
            new { patientId });
        var specimenTypeId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM specimen_type WHERE code = 'EDTA'");

        var exception = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(
            "INSERT INTO sample (order_id, barcode, specimen_type_id) VALUES (@orderId, @barcode, @specimenTypeId)",
            new { orderId, barcode = "92611A", specimenTypeId }));

        exception.SqlState.ShouldBe("23514");
    }
}

/// <summary>
/// Test PostgreSQL 16 compatibility (uuidv7 function).
/// </summary>
[Collection("Postgres16")]
public class MigratorPostgres16Tests(Postgres16Fixture fixture)
{
    [Fact]
    public async Task Upgrade_OnPostgres16_Works()
    {
        var connectionString = await fixture.NewEmptyDatabaseAsync();

        var result = Migrator.Upgrade(connectionString);
        result.Ok.ShouldBeTrue(result.Error);
        result.Applied.ShouldNotBeEmpty();

        // Verify migration is idempotent
        var second = Migrator.Upgrade(connectionString);
        second.Ok.ShouldBeTrue(second.Error);
        second.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task UuidV7Compat_GeneratesTimeOrderedUuid()
    {
        var connectionString = await fixture.NewDatabaseAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Generate two UUIDs with a small delay
        var uuid1 = await connection.ExecuteScalarAsync<Guid>(
            "SELECT uuidv7()");

        // Small delay to ensure different timestamp
        await Task.Delay(10, TestContext.Current.CancellationToken);

        var uuid2 = await connection.ExecuteScalarAsync<Guid>(
            "SELECT uuidv7()");

        // Convert to strings for comparison
        var str1 = uuid1.ToString();
        var str2 = uuid2.ToString();

        // Second UUID should be >= first (time-ordered)
        var cmp = string.Compare(str1, str2, StringComparison.Ordinal);
        cmp.ShouldBeLessThanOrEqualTo(0, $"UUID1: {str1}, UUID2: {str2} should be time-ordered");

        // Verify version digit is 7 (position 14 in string with dashes)
        str1[14].ShouldBe('7', "UUID version should be 7");
        str2[14].ShouldBe('7', "UUID version should be 7");

        // Verify UUIDs are not null and have correct format
        uuid1.ShouldNotBe(Guid.Empty);
        uuid2.ShouldNotBe(Guid.Empty);
        uuid1.ShouldNotBe(uuid2, "Generated UUIDs should be unique");
    }
}
