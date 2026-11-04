using System.Net.Sockets;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Local;
using Npgsql;

namespace Lis.Data.Setup;

/// <summary>
/// Kết quả thử kết nối CSDL ở bước 1 của trình hướng dẫn cài đặt lần đầu.
/// </summary>
/// <param name="Ok">true nếu kết nối thành công.</param>
/// <param name="Message">Thông báo tiếng Việt hiển thị cho người dùng (lỗi hoặc xác nhận thành công).</param>
/// <param name="DatabaseEmpty">true nếu kết nối được nhưng CSDL chưa chạy migration nào (CSDL trống).</param>
public record ConnectionTest(bool Ok, string Message, bool DatabaseEmpty);

/// <summary>
/// Các việc của trình hướng dẫn cài đặt lần đầu: thử kết nối CSDL, chạy migration,
/// kiểm tra/tạo tài khoản quản trị đầu tiên.
/// </summary>
public interface ISetupService
{
    /// <summary>
    /// Thử kết nối CSDL với timeout ngắn (~5 giây). Lỗi kết nối được dịch sang tiếng Việt.
    /// </summary>
    Task<ConnectionTest> TestAsync(DbSettings settings);

    /// <summary>Chạy migration (tạo/nâng cấp bảng) trên CSDL.</summary>
    MigrationResult Migrate(DbSettings settings);

    /// <summary>true nếu CSDL chưa có tài khoản nào — cần hiện bước tạo tài khoản quản trị đầu tiên.</summary>
    Task<bool> NeedsFirstAdminAsync(DbSettings settings);

    /// <summary>
    /// Tạo tài khoản quản trị đầu tiên, luôn gán vai trò <see cref="Roles.QuanTri"/>.
    /// Ném <see cref="InvalidOperationException"/> nếu CSDL đã có tài khoản.
    /// </summary>
    Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password);
}

/// <inheritdoc cref="ISetupService"/>
public sealed class SetupService : ISetupService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<ConnectionTest> TestAsync(DbSettings settings)
    {
        var connectionString = WithShortTimeout(settings);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var empty = Migrator.IsEmpty(connectionString);
            return new ConnectionTest(true, "Kết nối thành công", empty);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword)
        {
            return new ConnectionTest(false, "Sai tài khoản hoặc mật khẩu CSDL", false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new ConnectionTest(false, $"CSDL {settings.Database} chưa được tạo trên máy chủ", false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return new ConnectionTest(false, $"Không kết nối được máy chủ CSDL {settings.Host}:{settings.Port}", false);
        }
    }

    public MigrationResult Migrate(DbSettings settings) => Migrator.Upgrade(settings.ToConnectionString());

    public async Task<bool> NeedsFirstAdminAsync(DbSettings settings)
    {
        var store = CreateUserStore(settings);
        return !await store.AnyAsync();
    }

    public async Task CreateFirstAdminAsync(DbSettings settings, string username, string fullName, string password)
    {
        var store = CreateUserStore(settings);

        if (await store.AnyAsync())
        {
            throw new InvalidOperationException("CSDL đã có tài khoản, không thể tạo tài khoản quản trị đầu tiên lần nữa");
        }

        await store.CreateAsync(username, fullName, title: null, password, [Roles.QuanTri]);
    }

    private static UserStore CreateUserStore(DbSettings settings) => new(Db.Create(settings.ToConnectionString()));

    /// <summary>
    /// Lỗi không tới được máy chủ: không mở được socket (sai host/cổng, máy chủ không chạy)
    /// hoặc hết thời gian chờ kết nối. Đây là nhánh dự phòng — các lỗi CSDL trả về cụ thể
    /// (sai mật khẩu, không có CSDL) đã được bắt riêng ở trên.
    /// </summary>
    private static bool IsUnreachable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException or TimeoutException)
            {
                return true;
            }
        }

        return ex is NpgsqlException;
    }

    private static string WithShortTimeout(DbSettings settings)
    {
        var builder = new NpgsqlConnectionStringBuilder(settings.ToConnectionString())
        {
            Timeout = (int)ConnectTimeout.TotalSeconds,
        };
        return builder.ConnectionString;
    }
}
