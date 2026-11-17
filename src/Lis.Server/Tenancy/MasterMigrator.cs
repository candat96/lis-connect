using System.Reflection;
using DbUp;
using Lis.Data;

namespace Lis.Server.Tenancy;

/// <summary>
/// Chạy migration CSDL master (DbUp) từ các tệp <c>MasterMigrations/*.sql</c> nhúng trong assembly Lis.Server.
/// Cùng cách với <see cref="Migrator"/> của Lis.Data, nhưng danh sách script riêng của master.
/// </summary>
public static class MasterMigrator
{
    /// <summary>
    /// Áp dụng các migration master còn thiếu lên CSDL tại <paramref name="connectionString"/>. Chạy lại là an toàn:
    /// DbUp theo dõi script đã chạy trong bảng <c>schemaversions</c>.
    /// </summary>
    public static MigrationResult Upgrade(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.Contains(".MasterMigrations.", StringComparison.Ordinal))
            .LogToNowhere()
            // Như Lis.Data.Migrator: mỗi script một transaction, lỗi giữa chừng thì lần sau thử lại đúng script đó.
            .WithTransactionPerScript()
            .Build();

        var result = upgrader.PerformUpgrade();
        var applied = result.Scripts.Select(script => script.Name).ToList();

        return new MigrationResult(result.Successful, applied, result.Successful ? null : result.Error?.Message);
    }
}
