using Dapper;
using Lis.Core.Auth;
using Npgsql;

namespace Lis.Data.Auth;

/// <summary>
/// Thông tin tài khoản dùng để hiển thị/kiểm quyền, không chứa mật khẩu băm.
/// </summary>
public sealed record UserInfo(Guid Id, string Username, string FullName, string? Title, IReadOnlyList<string> Roles, bool Active);

/// <summary>
/// Truy cập dữ liệu tài khoản (bảng app_user): tạo, sửa, liệt kê.
/// Không xử lý đăng nhập — xem <see cref="AuthService"/>.
/// </summary>
public sealed class UserStore(Db db)
{
    private readonly PasswordHasher hasher = new();

    /// <summary>
    /// Hệ thống đã có tài khoản nào chưa (dùng để quyết định có cần tạo tài khoản quản trị đầu tiên).
    /// </summary>
    public async Task<bool> AnyAsync()
    {
        await using var connection = await db.OpenAsync();
        var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM app_user");
        return count > 0;
    }

    public async Task<UserInfo> CreateAsync(string username, string fullName, string? title, string password, IReadOnlyList<string> roles)
    {
        RequireValidPassword(password);

        var passwordHash = hasher.Hash(password);
        var rolesArray = roles.ToArray();

        await using var connection = await db.OpenAsync();
        try
        {
            var row = await connection.QuerySingleAsync<UserRow>(
                """
                INSERT INTO app_user (username, full_name, title, password_hash, roles)
                VALUES (@username, @fullName, @title, @passwordHash, @rolesArray)
                RETURNING id, username, full_name, title, roles, active
                """,
                new { username, fullName, title, passwordHash, rolesArray });

            return row.ToUserInfo();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Tên đăng nhập đã tồn tại", ex);
        }
    }

    public async Task UpdateAsync(Guid id, string fullName, string? title, IReadOnlyList<string> roles, bool active, string? newPassword)
    {
        var rolesArray = roles.ToArray();

        string? passwordHash = null;
        if (newPassword is not null)
        {
            RequireValidPassword(newPassword);
            passwordHash = hasher.Hash(newPassword);
        }

        // Kiểm "còn ít nhất một quản trị đang hoạt động" và ghi UPDATE phải nằm trong CÙNG một
        // giao dịch, có khóa hàng (FOR UPDATE) trên toàn bộ tập quản trị đang hoạt động — nếu
        // không, hai máy trạm cùng lúc mỗi bên khóa một quản trị khác nhau đều có thể đọc "còn
        // quản trị kia đang hoạt động" TRƯỚC khi bên kia ghi, rồi cả hai cùng commit, xóa sạch
        // quản trị đang hoạt động. FOR UPDATE buộc giao dịch thứ hai chờ giao dịch thứ nhất
        // commit/rollback rồi mới đọc lại đúng trạng thái mới nhất (Postgres re-check điều kiện
        // WHERE sau khi lấy được khóa, dưới mức cô lập READ COMMITTED mặc định).
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var activeAdminIds = (await connection.QueryAsync<Guid>(
                "SELECT id FROM app_user WHERE active = true AND @quanTri = ANY(roles) FOR UPDATE",
                new { quanTri = Roles.QuanTri }, transaction)).ToHashSet();

            var wasActiveAdmin = activeAdminIds.Contains(id);
            var willStayActiveAdmin = active && roles.Contains(Roles.QuanTri);

            if (wasActiveAdmin && !willStayActiveAdmin)
            {
                var otherActiveAdmins = activeAdminIds.Count - 1; // trừ chính tài khoản đang sửa
                if (otherActiveAdmins <= 0)
                {
                    throw new InvalidOperationException(
                        "Phải còn ít nhất một tài khoản Quản trị đang hoạt động — không thể khóa hoặc bỏ vai trò Quản trị của tài khoản này");
                }
            }

            if (passwordHash is null)
            {
                await connection.ExecuteAsync(
                    "UPDATE app_user SET full_name = @fullName, title = @title, roles = @rolesArray, active = @active WHERE id = @id",
                    new { id, fullName, title, rolesArray, active }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE app_user
                    SET full_name = @fullName, title = @title, roles = @rolesArray, active = @active, password_hash = @passwordHash
                    WHERE id = @id
                    """,
                    new { id, fullName, title, rolesArray, active, passwordHash }, transaction);
            }

            return true;
        });
    }

    public async Task<IReadOnlyList<UserInfo>> ListAsync()
    {
        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<UserRow>(
            "SELECT id, username, full_name, title, roles, active FROM app_user ORDER BY username");
        return rows.Select(row => row.ToUserInfo()).ToList();
    }

    private static void RequireValidPassword(string password)
    {
        if (password.Length < 8)
        {
            throw new ArgumentException("Mật khẩu tối thiểu 8 ký tự", nameof(password));
        }
    }

    private sealed record UserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string FullName { get; init; } = "";
        public string? Title { get; init; }
        public string[] Roles { get; init; } = [];
        public bool Active { get; init; }

        public UserInfo ToUserInfo() => new(Id, Username, FullName, Title, Roles, Active);
    }
}
