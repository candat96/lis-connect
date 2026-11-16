using System.Text.RegularExpressions;
using Lis.Core.Auth;
using Lis.Data.Auth;

namespace Lis.Server.Api;

public sealed record CreateUserRequest(string? Username, string? FullName, string? Title, string[]? Roles, string? Password);

public sealed record UpdateUserRequest(string? FullName, string? Title, string[]? Roles, bool? Active, string? NewPassword);

public sealed record ChangeOwnPasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserView(
    Guid Id, string Username, string FullName, string? Title, IReadOnlyList<string> Roles, bool Active,
    string? LockedUntil, string? LastLoginAt);

/// <summary>
/// Nhân sự (tài khoản đăng nhập) của phòng khám (<c>/api/users</c>): chỉ Quản trị được quản lý; ai cũng tự đổi được
/// mật khẩu của mình. Quy tắc mật khẩu, băm, "luôn còn một Quản trị đang hoạt động" nằm ở <see cref="UserStore"/>.
/// Kiểm đầu vào chạy trước kiểm quyền, giống pipe chạy trước handler ở NestJS.
/// </summary>
public static partial class UsersEndpoints
{
    private const string PasswordTooShort = "Mật khẩu tối thiểu 8 ký tự";
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";

    public static void MapUsersApi(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireSession();
        users.MapGet("", (HttpContext http, TimeProvider clock) => ListAsync(http.Ctx(), clock));
        users.MapPost("", (HttpContext http, CreateUserRequest body) => CreateAsync(http.Ctx(), body));
        users.MapPut("/{id}", (HttpContext http, string id, UpdateUserRequest body) => UpdateAsync(http.Ctx(), id, body));
        users.MapPost("/me/password", (HttpContext http, ChangeOwnPasswordRequest body) => ChangeOwnPasswordAsync(http.Ctx(), body));
    }

    private static async Task<IResult> ListAsync(RequestContext ctx, TimeProvider clock)
    {
        ctx.RequireAdmin();
        var now = clock.GetUtcNow();
        var details = await ctx.Tenant.Users.ListDetailsAsync();
        return Results.Ok(details.Select(d => new UserView(
            d.User.Id, d.User.Username, d.User.FullName, d.User.Title, d.User.Roles, d.User.Active,
            d.LockedUntil is { } locked && locked > now ? Iso(locked) : null,
            d.LastLoginAt is { } last ? Iso(last) : null)));
    }

    private static async Task<IResult> CreateAsync(RequestContext ctx, CreateUserRequest body)
    {
        var username = body.Username ?? throw ApiException.BadRequest(InvalidBody);
        if (username.Length == 0) throw ApiException.BadRequest("Nhập tên đăng nhập");
        if (username.Length > 64) throw ApiException.BadRequest("Tên đăng nhập tối đa 64 ký tự");
        if (!UsernamePattern().IsMatch(username))
        {
            throw ApiException.BadRequest("Tên đăng nhập chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới");
        }

        var fullName = CheckFullName(body.FullName);
        CheckTitle(body.Title);
        var roles = CheckRoles(body.Roles);
        var password = body.Password ?? throw ApiException.BadRequest(InvalidBody);
        if (password.Length > 256) throw ApiException.BadRequest("Mật khẩu tối đa 256 ký tự");

        ctx.RequireAdmin();
        CheckPassword(password);
        try
        {
            var user = await ctx.Tenant.Users.CreateAsync(username.Trim(), fullName, NormalTitle(body.Title), password, roles);
            return Results.Json(new { id = user.Id }, statusCode: StatusCodes.Status201Created);
        }
        catch (InvalidOperationException)
        {
            // UserStore chỉ ném InvalidOperationException khi trùng tên đăng nhập.
            throw ApiException.Conflict("Tên đăng nhập đã tồn tại");
        }
    }

    private static async Task<IResult> UpdateAsync(RequestContext ctx, string id, UpdateUserRequest body)
    {
        if (!Guid.TryParseExact(id, "D", out var userId)) throw ApiException.BadRequest("Validation failed (uuid is expected)");

        var fullName = CheckFullName(body.FullName);
        CheckTitle(body.Title);
        var roles = CheckRoles(body.Roles);
        var active = body.Active ?? throw ApiException.BadRequest(InvalidBody);
        if (body.NewPassword is { Length: > 256 }) throw ApiException.BadRequest("Mật khẩu tối đa 256 ký tự");

        ctx.RequireAdmin();
        // Chuỗi rỗng = không đặt lại mật khẩu (như `if (body.newPassword)` ở NestJS).
        var newPassword = string.IsNullOrEmpty(body.NewPassword) ? null : body.NewPassword;
        if (newPassword is not null) CheckPassword(newPassword);

        var result = await ctx.Tenant.Users.UpdateAccountAsync(userId, fullName, NormalTitle(body.Title), roles, active, newPassword);
        return result switch
        {
            UserUpdateResult.Updated => Results.NoContent(),
            UserUpdateResult.NotFound => throw ApiException.NotFound("Không tìm thấy tài khoản"),
            _ => throw ApiException.Conflict(
                "Phải còn ít nhất một tài khoản Quản trị đang hoạt động — không thể khoá hoặc bỏ vai trò Quản trị của tài khoản này"),
        };
    }

    /// <summary>Tự đổi mật khẩu của mình (mọi vai trò); phải nhập đúng mật khẩu hiện tại.</summary>
    private static async Task<IResult> ChangeOwnPasswordAsync(RequestContext ctx, ChangeOwnPasswordRequest body)
    {
        var current = body.CurrentPassword ?? throw ApiException.BadRequest(InvalidBody);
        if (current.Length == 0) throw ApiException.BadRequest("Nhập mật khẩu hiện tại");
        if (current.Length > 256) throw ApiException.BadRequest("Mật khẩu hiện tại tối đa 256 ký tự");
        var newPassword = body.NewPassword ?? throw ApiException.BadRequest(InvalidBody);
        if (newPassword.Length > 256) throw ApiException.BadRequest("Mật khẩu tối đa 256 ký tự");

        CheckPassword(newPassword);
        if (!await ctx.Tenant.Users.ChangePasswordAsync(ctx.User.Id, current, newPassword))
        {
            throw ApiException.BadRequest("Mật khẩu hiện tại không đúng");
        }

        return Results.NoContent();
    }

    private static string CheckFullName(string? fullName)
    {
        if (fullName is null) throw ApiException.BadRequest(InvalidBody);
        if (fullName.Length == 0) throw ApiException.BadRequest("Nhập họ tên");
        if (fullName.Length > 100) throw ApiException.BadRequest("Họ tên tối đa 100 ký tự");
        return fullName.Trim();
    }

    private static void CheckTitle(string? title)
    {
        if (title is { Length: > 100 }) throw ApiException.BadRequest("Chức danh tối đa 100 ký tự");
    }

    private static string? NormalTitle(string? title) => string.IsNullOrEmpty(title?.Trim()) ? null : title.Trim();

    private static string[] CheckRoles(string[]? roles)
    {
        if (roles is null) throw ApiException.BadRequest("Chọn ít nhất một vai trò");
        if (roles.Length == 0) throw ApiException.BadRequest("Chọn ít nhất một vai trò");
        if (roles.Any(r => r is null || !Roles.All.Contains(r))) throw ApiException.BadRequest("Vai trò không hợp lệ");
        return roles.Distinct().ToArray();
    }

    private static void CheckPassword(string password)
    {
        if (password.Length < 8) throw ApiException.BadRequest(PasswordTooShort);
    }

    /// <summary>Dạng <c>toISOString()</c> của JS: UTC, đủ mili giây.</summary>
    internal static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex UsernamePattern();
}
