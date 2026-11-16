using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Một container PostgreSQL cho cả lớp test: CSDL master + CSDL từng phòng khám tạo trong đó.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string MasterConnection => WithDatabase("lis_master_test");

    /// <summary>Mẫu chuỗi kết nối phòng khám (<c>{db}</c> = tên CSDL).</summary>
    public string TenantTemplate => WithDatabase("{db}");

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string WithDatabase(string database) =>
        new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = "DBNAME", Pooling = false }
            .ConnectionString.Replace("DBNAME", database, StringComparison.Ordinal);
}
