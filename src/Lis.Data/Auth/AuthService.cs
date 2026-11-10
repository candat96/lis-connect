using Dapper;
using Npgsql;

namespace Lis.Data.Auth;

/// <summary>
/// Kết quả đăng nhập. <see cref="Code"/> nhận một trong: <c>ok</c>, <c>sai</c>, <c>khoa</c>, <c>ngung</c>.
/// </summary>
public sealed record LoginResult(bool Ok, string Code, string Message, UserInfo? User);

/// <summary>
/// Phần của <see cref="AuthService"/> cần cho <c>LoginViewModel</c> — tách interface để test
/// được bằng một bản giả (vd mô phỏng CSDL mất kết nối), không cần CSDL thật.
/// </summary>
public interface IAuthService
{
    Task<LoginResult> LoginAsync(string username, string password);
}

/// <summary>
/// Xử lý đăng nhập: kiểm mật khẩu, khóa tài khoản sau 5 lần sai liên tiếp trong 15 phút,
/// chặn tài khoản đã ngừng hoạt động.
/// </summary>
public sealed class AuthService(Db db, PasswordHasher hasher, TimeProvider clock) : IAuthService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // Hash hợp lệ về định dạng nhưng không khớp mật khẩu nào — dùng để giữ thời gian xử lý
    // ổn định khi tên đăng nhập không tồn tại (xem LoginAsync).
    private static readonly string DummyHashForTimingSafety =
        $"argon2id$65536$3$1${Convert.ToBase64String(new byte[16])}${Convert.ToBase64String(new byte[32])}";

    /// <summary>
    /// Dấu phiên cho "ghi nhớ đăng nhập": băm SHA-256 của password_hash. Đổi mật khẩu → dấu đổi →
    /// phiên đã ghi nhớ ở mọi máy hết hiệu lực. Tài khoản không có hoặc đã ngừng → null.
    /// </summary>
    public async Task<string?> SessionStampAsync(Guid userId)
    {
        await using var connection = await db.OpenAsync();
        var hash = await connection.ExecuteScalarAsync<string?>(
            "SELECT password_hash FROM app_user WHERE id = @userId AND active", new { userId });
        return hash is null ? null : Stamp(hash);
    }

    /// <summary>Vào lại bằng phiên đã ghi nhớ: tài khoản còn hoạt động, không bị khoá, dấu phiên còn khớp → trả tài khoản.</summary>
    public async Task<UserInfo?> ResumeAsync(Guid userId, string stamp)
    {
        await using var connection = await db.OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<LoginRow>(
            """
            SELECT id, username, full_name, title, password_hash, roles, active, failed_count, locked_until
            FROM app_user
            WHERE id = @userId
            """,
            new { userId });

        var now = clock.GetUtcNow();
        if (row is null || !row.Active || (row.LockedUntil is { } lockedUntil && lockedUntil > now)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(Stamp(row.PasswordHash)), System.Text.Encoding.ASCII.GetBytes(stamp)))
        {
            return null;
        }

        await connection.ExecuteAsync("UPDATE app_user SET last_login_at = @now WHERE id = @userId", new { userId, now });
        return new UserInfo(row.Id, row.Username, row.FullName, row.Title, row.Roles, row.Active);
    }

    private static string Stamp(string passwordHash) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("lis-phien|" + passwordHash)));

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        await using var connection = await db.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync<LoginRow>(
            """
            SELECT id, username, full_name, title, password_hash, roles, active, failed_count, locked_until
            FROM app_user
            WHERE username = @username
            """,
            new { username });

        if (row is null)
        {
            // Vẫn chạy một lần Verify với hash giả để thời gian xử lý giống trường hợp
            // tài khoản có thật — tránh lộ thông tin "tên đăng nhập không tồn tại" qua thời gian phản hồi.
            hasher.Verify(password, DummyHashForTimingSafety);
            return InvalidCredentialsResult();
        }

        var now = clock.GetUtcNow();

        if (row.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return LockedResult(lockedUntil, now);
        }

        if (!row.Active)
        {
            return new LoginResult(false, "ngung", "Tài khoản đã ngừng hoạt động", null);
        }

        if (!hasher.Verify(password, row.PasswordHash))
        {
            return await RecordFailureAndBuildResultAsync(connection, row, now);
        }

        await connection.ExecuteAsync(
            "UPDATE app_user SET failed_count = 0, locked_until = NULL, last_login_at = @now WHERE id = @id",
            new { row.Id, now });

        var user = new UserInfo(row.Id, row.Username, row.FullName, row.Title, row.Roles, row.Active);
        return new LoginResult(true, "ok", "Đăng nhập thành công", user);
    }

    /// <summary>
    /// Tăng failed_count và (nếu tới ngưỡng) đặt locked_until nguyên tử trong một câu UPDATE —
    /// tránh lost-update khi nhiều máy trạm đăng nhập sai cùng lúc (đọc-tăng-ghi ở C# không an toàn
    /// với tương tranh; UPDATE ... RETURNING dựa trên giá trị cột hiện tại của Postgres mới an toàn).
    /// </summary>
    private async Task<LoginResult> RecordFailureAndBuildResultAsync(NpgsqlConnection connection, LoginRow row, DateTimeOffset now)
    {
        var lockedUntilCandidate = now + LockDuration;

        var updated = await connection.QuerySingleAsync<FailureUpdateRow>(
            """
            UPDATE app_user
            SET failed_count = failed_count + 1,
                locked_until = CASE WHEN failed_count + 1 >= @maxFailedAttempts THEN @lockedUntilCandidate ELSE locked_until END
            WHERE id = @id
            RETURNING failed_count, locked_until
            """,
            new { row.Id, maxFailedAttempts = MaxFailedAttempts, lockedUntilCandidate });

        return updated.FailedCount >= MaxFailedAttempts && updated.LockedUntil is { } lockedUntil
            ? LockedResult(lockedUntil, now)
            : InvalidCredentialsResult();
    }

    private static LoginResult InvalidCredentialsResult() =>
        new(false, "sai", "Sai tên đăng nhập hoặc mật khẩu", null);

    private static LoginResult LockedResult(DateTimeOffset lockedUntil, DateTimeOffset now)
    {
        var minutesLeft = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
        return new LoginResult(false, "khoa", $"Tài khoản tạm khóa do nhập sai nhiều lần, thử lại sau {minutesLeft} phút", null);
    }

    private sealed record LoginRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string FullName { get; init; } = "";
        public string? Title { get; init; }
        public string PasswordHash { get; init; } = "";
        public string[] Roles { get; init; } = [];
        public bool Active { get; init; }
        public int FailedCount { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
    }

    private sealed record FailureUpdateRow
    {
        public int FailedCount { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
    }
}
