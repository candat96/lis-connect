using System.Text;
using Npgsql;

namespace Lis.Data.Local;

/// <summary>
/// Thông tin kết nối CSDL PostgreSQL lưu trên máy trạm.
/// </summary>
public record DbSettings(string Host, int Port, string Database, string Username, string Password, bool RequireSsl)
{
    /// <summary>
    /// Chuỗi kết nối Npgsql. Khi <see cref="RequireSsl"/> bật thì bắt buộc TLS (Require),
    /// ngược lại chỉ ưu tiên dùng TLS nếu máy chủ hỗ trợ (Prefer). Dùng
    /// <see cref="NpgsqlConnectionStringBuilder"/> (không tự nối chuỗi) để mật khẩu/tên CSDL chứa
    /// <c>;</c>, <c>=</c> hoặc <c>"</c> vẫn được escape đúng, không làm hỏng chuỗi kết nối.
    /// </summary>
    public string ToConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = RequireSsl ? SslMode.Require : SslMode.Prefer,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Ghi đè để che mật khẩu trong ToString() — tránh lộ mật khẩu qua log/thông báo lỗi/debugger.
    /// </summary>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Host = {Host}, Port = {Port}, Database = {Database}, Username = {Username}, Password = ***, RequireSsl = {RequireSsl}");
        return true;
    }
}

/// <summary>
/// Cấu hình cục bộ của máy trạm LIS Connect: thông tin CSDL và mã máy trạm.
/// </summary>
public record LocalConfig(DbSettings Db, string WorkstationCode);
