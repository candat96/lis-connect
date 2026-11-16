using System.Text.Json;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Server.Tenancy;
using Microsoft.AspNetCore.Http.Metadata;

namespace Lis.Server.Api;

/// <summary>Ngữ cảnh một request đã đăng nhập: phòng khám, dịch vụ của CSDL phòng khám, tài khoản.</summary>
public sealed record RequestContext(Clinic Clinic, TenantServices Tenant, UserInfo User);

/// <summary>Nhóm route <c>/api</c>: bắt lỗi, cấm trường lạ, bắt buộc phiên đăng nhập, kiểm quyền theo vai trò.</summary>
public static class ApiSession
{
    private const string ContextItem = "lis-request-context";
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// Nhóm <c>/api</c>: lỗi <see cref="ApiException"/> trả JSON <c>{statusCode, message}</c>; thân request JSON có
    /// trường lạ → 400. Chỉ áp cho <c>/api</c>, không đụng <c>/agent/v1</c>. Gọi một lần lúc dựng ứng dụng.
    /// </summary>
    public static RouteGroupBuilder MapApiGroup(this WebApplication app)
    {
        app.UseWhen(http => http.Request.Path.StartsWithSegments("/api"), branch => branch.Use(ApiErrors.HandleAsync));

        return NewApiGroup(app);
    }

    /// <summary>Tạo nhóm <c>/api</c> (không dựng middleware) — <see cref="MapApiGroup"/> dùng, và test dựng thêm route thử.</summary>
    internal static RouteGroupBuilder NewApiGroup(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api");
        group.AddEndpointFilter(RejectUnknownMembersAsync);
        return group;
    }

    /// <summary>Đánh dấu route cần phiên; <see cref="ApiErrors.HandleAsync"/> kiểm phiên TRƯỚC khi bind thân request.</summary>
    private sealed record SessionRequired;

    /// <summary>
    /// Bắt buộc đăng nhập (header <c>Authorization: Bearer</c>) và gắn <see cref="RequestContext"/> — đọc bằng <see cref="Ctx"/>.
    /// Kiểm phiên chạy trước cả bind thân request (như guard chạy trước pipe ở NestJS): chưa đăng nhập thì luôn 401,
    /// dù thân gửi lên hỏng hay có trường lạ. Tài khoản bị ngừng sau khi đăng nhập thì request kế tiếp bị từ chối ngay.
    /// </summary>
    public static RouteGroupBuilder RequireSession(this RouteGroupBuilder group) => group.WithMetadata(new SessionRequired());

    internal static bool RequiresSession(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<SessionRequired>() is not null;

    internal static async Task AuthenticateAsync(HttpContext http)
    {
        var services = http.RequestServices;

        var session = services.GetRequiredService<SessionTokens>().Read(http.Request.Headers.Authorization.FirstOrDefault())
            ?? throw ApiException.Unauthorized("Phiên đăng nhập hết hạn — đăng nhập lại");

        var clinic = await services.GetRequiredService<MasterStore>().FindClinicByIdAsync(session.ClinicId)
            ?? throw ApiException.Unauthorized("Phiên đăng nhập không hợp lệ");

        var tenant = services.GetRequiredService<TenantRegistry>().For(clinic);
        var user = await tenant.Users.FindAsync(session.UserId);
        if (user is not { Active: true })
        {
            throw ApiException.Unauthorized("Tài khoản đã ngừng hoạt động");
        }

        http.Items[ContextItem] = new RequestContext(clinic, tenant, user);
    }

    public static RequestContext Ctx(this HttpContext http) =>
        http.Items[ContextItem] as RequestContext ?? throw new InvalidOperationException("Route chưa gọi RequireSession()");

    /// <summary>Cài đặt hệ thống (máy, map kênh, agent): chỉ Quản trị.</summary>
    public static void RequireAdmin(this RequestContext ctx) =>
        Require(ctx, "Chỉ tài khoản Quản trị được thay đổi cài đặt này", Roles.QuanTri);

    /// <summary>Nhập, sửa, duyệt kết quả; chấp nhận / bỏ giá trị máy gửi lại: Quản trị, Kỹ thuật viên, Trưởng khoa.</summary>
    public static void RequireResultEditor(this RequestContext ctx) =>
        Require(ctx, "Tài khoản không có quyền nhập / duyệt kết quả", Roles.QuanTri, Roles.KyThuatVien, Roles.TruongKhoa);

    /// <summary>Huỷ duyệt một mẫu đã duyệt: chỉ Quản trị và Trưởng khoa.</summary>
    public static void RequireUnapprover(this RequestContext ctx) =>
        Require(ctx, "Chỉ Trưởng khoa hoặc Quản trị được huỷ duyệt", Roles.QuanTri, Roles.TruongKhoa);

    /// <summary>Theo dõi máy, xem log: Quản trị, Kỹ thuật viên, Trưởng khoa.</summary>
    public static void RequireLabStaff(this RequestContext ctx) =>
        Require(ctx, "Tài khoản không có quyền xem mục này", Roles.QuanTri, Roles.KyThuatVien, Roles.TruongKhoa);

    private static void Require(RequestContext ctx, string message, params string[] allowed)
    {
        if (!ctx.User.Roles.Any(allowed.Contains))
        {
            throw ApiException.Forbidden(message);
        }
    }

    /// <summary>
    /// Bind JSON của minimal API bỏ qua trường lạ; sau khi bind, đọc lại thân (đã bật buffering) bằng bộ cấu hình
    /// nghiêm ngặt theo kiểu thân route để từ chối trường lạ (NestJS: forbidNonWhitelisted).
    /// </summary>
    private static async ValueTask<object?> RejectUnknownMembersAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var bodyType = http.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
        if (bodyType is not null && http.Request.ContentLength is not 0 && http.Request.HasJsonContentType())
        {
            http.Request.Body.Seek(0, SeekOrigin.Begin);
            try
            {
                await JsonSerializer.DeserializeAsync(http.Request.Body, bodyType, StrictJson, http.RequestAborted);
            }
            catch (JsonException)
            {
                throw ApiException.BadRequest("Dữ liệu gửi lên có trường không hợp lệ");
            }
        }

        return await next(context);
    }
}
