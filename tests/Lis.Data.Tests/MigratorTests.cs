using Dapper;
using Npgsql;
using Shouldly;
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
