using System.Reflection;
using DbUp;
using Npgsql;

namespace Lis.Data;

/// <summary>
/// Kết quả một lần chạy migration.
/// </summary>
/// <param name="Ok">true nếu mọi script đã chạy thành công (hoặc không có gì để chạy).</param>
/// <param name="Applied">Tên các script vừa được áp dụng; rỗng nếu CSDL đã ở bản mới nhất.</param>
/// <param name="Error">Thông báo lỗi nếu <see cref="Ok"/> là false, ngược lại null.</param>
public record MigrationResult(bool Ok, IReadOnlyList<string> Applied, string? Error);

/// <summary>
/// Chạy migration CSDL (DbUp) từ các tệp .sql nhúng (embedded resource) trong assembly Lis.Data.
/// </summary>
public static class Migrator
{
    /// <summary>
    /// Áp dụng tất cả migration còn thiếu lên CSDL tại <paramref name="connectionString"/>.
    /// Chạy nhiều lần là an toàn (idempotent) — DbUp tự theo dõi script đã chạy trong bảng
    /// schemaversions, lần gọi sau không còn gì để áp dụng.
    /// </summary>
    public static MigrationResult Upgrade(string connectionString)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        var applied = result.Scripts.Select(script => script.Name).ToList();

        return new MigrationResult(result.Successful, applied, result.Successful ? null : result.Error?.Message);
    }

    /// <summary>
    /// true nếu CSDL chưa chạy migration nào (chưa có bảng schemaversions do DbUp tạo).
    /// </summary>
    public static bool IsEmpty(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT NOT EXISTS (SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = 'schemaversions')";

        return (bool)command.ExecuteScalar()!;
    }
}
