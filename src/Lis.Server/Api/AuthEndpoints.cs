using System.Text.RegularExpressions;
using Lis.Data.Auth;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

public sealed record LoginRequest(string? ClinicCode, string? Username, string? Password);

public sealed record SessionClinic(string Code, string Name);

public sealed record SessionUser(Guid Id, string Username, string FullName, string? Title, IReadOnlyList<string> Roles);

public sealed record SessionInfo(SessionClinic Clinic, SessionUser User);

public sealed record LoginResponse(string Token, SessionClinic Clinic, SessionUser User);

/// <summary>
/// Đăng nhập web theo phòng khám (<c>/api/auth</c>). Quy tắc kiểm mật khẩu và khoá tài khoản do
/// <see cref="AuthService"/> (Lis.Data) đảm nhiệm; ở đây chỉ chọn phòng khám, đổi mã kết quả thành HTTP và cấp token.
/// </summary>
public static partial class AuthEndpoints
{
    private const string Invalid = "Sai mã phòng khám, tên đăng nhập hoặc mật khẩu";

    // Hash hợp lệ định dạng, không khớp mật khẩu nào: giữ thời gian xử lý ổn định khi mã phòng khám không tồn tại.
    private static readonly string DummyHash =
        $"argon2id$65536$3$1${Convert.ToBase64String(new byte[16])}${Convert.ToBase64String(new byte[32])}";

    public static void MapAuthApi(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapPost("/login", LoginAsync);
        auth.MapGroup("").RequireSession().MapGet("/me", (HttpContext http) => Me(http.Ctx()));
    }

    private static async Task<IResult> LoginAsync(LoginRequest body, HttpContext http, MasterStore master, TenantRegistry tenants, SessionTokens tokens)
    {
        if (string.IsNullOrEmpty(body.ClinicCode)) throw ApiException.BadRequest("Chưa nhập mã phòng khám");
        if (string.IsNullOrEmpty(body.Username)) throw ApiException.BadRequest("Chưa nhập tên đăng nhập");
        if (string.IsNullOrEmpty(body.Password)) throw ApiException.BadRequest("Chưa nhập mật khẩu");
        if (body.ClinicCode.Length > 64) throw ApiException.BadRequest("Mã phòng khám tối đa 64 ký tự");
        if (body.Username.Length > 128) throw ApiException.BadRequest("Tên đăng nhập tối đa 128 ký tự");
        if (body.Password.Length > 256) throw ApiException.BadRequest("Mật khẩu tối đa 256 ký tự");

        var clinic = await master.FindClinicAsync(body.ClinicCode);
        if (clinic is null)
        {
            new PasswordHasher().Verify(body.Password, DummyHash);
            throw ApiException.Unauthorized(Invalid);
        }

        ClinicUpgradeFailure.ThrowIfFailed(http.RequestServices, clinic);

        var result = await tenants.For(clinic).Auth.LoginAsync(body.Username.Trim(), body.Password);
        if (!result.Ok)
        {
            throw result.Code switch
            {
                "khoa" => ApiException.TooManyRequests(
                    $"Tài khoản tạm khoá do nhập sai nhiều lần — thử lại sau {MinutesIn(result.Message)} phút"),
                "ngung" => ApiException.Unauthorized("Tài khoản đã ngừng hoạt động"),
                _ => ApiException.Unauthorized(Invalid),
            };
        }

        var user = result.User!;
        return Results.Ok(new LoginResponse(tokens.Issue(user.Id, clinic.Id), ToClinic(clinic), ToUser(user)));
    }

    private static SessionInfo Me(RequestContext ctx) => new(ToClinic(ctx.Clinic), ToUser(ctx.User));

    private static SessionClinic ToClinic(Clinic clinic) => new(clinic.Code, clinic.Name);

    private static SessionUser ToUser(UserInfo user) => new(user.Id, user.Username, user.FullName, user.Title, user.Roles);

    // AuthService chỉ trả câu chữ ("…thử lại sau N phút"); lấy N ra để dựng đúng câu của hợp đồng /api.
    private static int MinutesIn(string message) =>
        MinutesPattern().Match(message) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 15;

    [GeneratedRegex(@"sau (\d+) phút")]
    private static partial Regex MinutesPattern();
}
