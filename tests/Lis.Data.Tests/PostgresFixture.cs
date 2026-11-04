using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lis.Data.Tests;

/// <summary>
/// Dùng chung một container PostgreSQL (postgres:18-alpine) cho cả lần chạy test, để tránh
/// khởi động container nhiều lần. Mỗi lớp test gọi <see cref="NewDatabaseAsync"/> để lấy
/// một CSDL riêng, đã migrate sẵn, không dùng chung dữ liệu với lớp test khác.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine")
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
    /// Tạo một CSDL mới (tên ngẫu nhiên), còn trống, chưa chạy migration — dùng cho các test
    /// cần kiểm tra trạng thái trước/trong lúc migrate (<see cref="Migrator.IsEmpty"/>,
    /// <see cref="Migrator.Upgrade"/>).
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

        // Mỗi test tạo một Db (một pool kết nối) riêng và không bao giờ dispose; kết nối nhàn rỗi
        // trong pool giữ mở tới hết tiến trình, cộng dồn qua hàng chục test là vượt
        // max_connections (100) của container. Tắt pool để kết nối đóng thật khi trả về.
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        return connectionStringBuilder.ConnectionString;
    }
}
