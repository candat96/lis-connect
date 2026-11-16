using System.Text.RegularExpressions;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

public sealed record CreateClinicRequest(
    string? SetupKey, string? Code, string? Name, string? AdminUsername, string? AdminFullName, string? AdminPassword);

/// <summary>
/// Thiết lập phòng khám đầu tiên từ web (<c>/api/setup</c>, không cần đăng nhập): xem đã có phòng khám chưa, và tạo phòng khám
/// kèm tài khoản Quản trị đầu tiên khi nhập đúng mã thiết lập.
/// </summary>
public static partial class SetupEndpoints
{
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";

    public static void MapSetupApi(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup");
        setup.MapGet("/status", async (MasterStore master) => Results.Ok(new { hasClinic = (await master.ClinicsAsync()).Count > 0 }));
        setup.MapPost("/clinic", CreateClinicAsync);
    }

    private static async Task<IResult> CreateClinicAsync(
        HttpContext http, CreateClinicRequest body, SetupGuard guard, ClinicProvisioner provisioner)
    {
        await guard.CheckAsync(http.Connection.RemoteIpAddress?.ToString() ?? "không rõ", body.SetupKey, http.RequestAborted);

        var code = body.Code ?? throw ApiException.BadRequest(InvalidBody);
        if (code.Length == 0) throw ApiException.BadRequest("Nhập mã phòng khám");
        if (!ClinicCodePattern().IsMatch(code))
        {
            throw ApiException.BadRequest("Mã phòng khám chỉ gồm chữ thường không dấu, số, gạch ngang (2–32 ký tự)");
        }

        var name = (body.Name ?? throw ApiException.BadRequest(InvalidBody)).Trim();
        if (name.Length == 0) throw ApiException.BadRequest("Nhập tên phòng khám");
        if (name.Length > 200) throw ApiException.BadRequest("Tên phòng khám tối đa 200 ký tự");

        var username = UsersEndpoints.CheckUsername(body.AdminUsername);
        var fullName = UsersEndpoints.CheckFullName(body.AdminFullName);
        var password = UsersEndpoints.CheckNewPassword(body.AdminPassword);

        var clinic = await provisioner.CreateAsync(code, name, username, fullName, password);
        return Results.Json(new { code = clinic.Code }, statusCode: StatusCodes.Status201Created);
    }

    [GeneratedRegex(@"^[a-z0-9-]{2,32}\z")]
    private static partial Regex ClinicCodePattern();
}
