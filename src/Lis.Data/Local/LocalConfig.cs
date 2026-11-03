namespace Lis.Data.Local;

/// <summary>
/// Thông tin kết nối CSDL PostgreSQL lưu trên máy trạm.
/// </summary>
public record DbSettings(string Host, int Port, string Database, string Username, string Password, bool RequireSsl)
{
    /// <summary>
    /// Chuỗi kết nối Npgsql. Khi <see cref="RequireSsl"/> bật thì bắt buộc TLS (Require),
    /// ngược lại chỉ ưu tiên dùng TLS nếu máy chủ hỗ trợ (Prefer).
    /// </summary>
    public string ToConnectionString()
    {
        var sslMode = RequireSsl ? "Require" : "Prefer";
        return $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};SSL Mode={sslMode}";
    }
}

/// <summary>
/// Cấu hình cục bộ của máy trạm LIS Connect: thông tin CSDL và mã máy trạm.
/// </summary>
public record LocalConfig(DbSettings Db, string WorkstationCode);
