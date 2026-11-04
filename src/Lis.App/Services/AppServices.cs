using Dapper;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Local;

namespace Lis.App.Services;

/// <summary>
/// Các service của ứng dụng giao diện, khởi tạo từ <see cref="LocalConfig"/> đã lưu trên máy trạm:
/// kết nối CSDL (<see cref="Db"/>) và các store/service dùng chung (tài khoản, đăng nhập, cơ sở).
/// </summary>
public sealed class AppServices
{
    public AppServices(LocalConfig config)
    {
        Config = config;
        Db = Db.Create(config.Db.ToConnectionString());
        Users = new UserStore(Db);
        Auth = new AuthService(Db, new PasswordHasher(), TimeProvider.System);
        Facility = new FacilityStore(Db);
        Catalog = new CatalogStore(Db);
    }

    public LocalConfig Config { get; }

    public Db Db { get; }

    public UserStore Users { get; }

    public AuthService Auth { get; }

    public FacilityStore Facility { get; }

    public CatalogStore Catalog { get; }

    /// <summary>
    /// Cập nhật máy trạm (<c>workstation.last_seen_at</c>) ngay sau khi đăng nhập thành công.
    /// Tạo dòng mới nếu máy trạm chưa có trong CSDL.
    /// </summary>
    public async Task TouchWorkstationAsync()
    {
        await using var connection = await Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO workstation (code, name, last_seen_at)
            VALUES (@code, @code, now())
            ON CONFLICT (code) DO UPDATE SET last_seen_at = now()
            """,
            new { code = Config.WorkstationCode });
    }
}
