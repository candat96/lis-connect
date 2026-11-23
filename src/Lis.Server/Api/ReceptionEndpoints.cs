using Lis.Data.Facility;
using Lis.Data.Orders;
using Lis.Server.Tenancy;

namespace Lis.Server.Api;

/// <summary>Người bệnh nhập mới ở màn Tiếp nhận. <c>sex</c>: M, F hoặc O; cần <c>dob</c> hoặc <c>birthYear</c>.</summary>
public sealed record ReceptionPatientBody(string? FullName, DateOnly? Dob, int? BirthYear, string? Sex, string? Phone, string? Address);

/// <summary>Chỉ định mới: đúng một trong <c>patientId</c> / <c>patient</c>, và 1–50 dịch vụ (<c>serviceIds</c>).</summary>
public sealed record ReceptionOrderBody(
    string? PatientId, ReceptionPatientBody? Patient, List<string>? ServiceIds, string? OrderingDoctor, string? OrderingDept, string? Diagnosis);

/// <summary>
/// Tiếp nhận tối giản (<c>/api/reception</c>): Tiếp nhận và Quản trị tìm người bệnh, tạo chỉ định. Mọi chỉ định đi qua
/// <see cref="OrderService.CreateAsync"/> — cùng quy tắc mã người bệnh, tách panel, gom mẫu và sinh barcode với <c>Lis.App</c>.
/// </summary>
public static class ReceptionEndpoints
{
    private const int MinQueryLength = 2;
    private const int MaxNameLength = 200;
    private const int MaxServices = 50;
    private const int MaxShortText = 200;
    private const int MaxAddress = 500;
    private const int MinBirthYear = 1900;

    public static void MapReceptionApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reception").RequireSession();
        group.MapGet("/patients", (Delegate)((HttpContext http) => FindPatientsAsync(http.Ctx(), http.Request.Query["q"].ToString())));
        group.MapPost("/orders", (HttpContext http, ReceptionOrderBody body) => CreateOrderAsync(http.Ctx(), body));
    }

    private static async Task<IResult> FindPatientsAsync(RequestContext ctx, string q)
    {
        ctx.RequireReception();
        var query = q.Trim();
        if (query.Length < MinQueryLength)
        {
            return Results.Ok(Array.Empty<PatientHit>());
        }

        return Results.Ok(await ctx.Tenant.Orders.FindPatientsAsync(query));
    }

    private static async Task<IResult> CreateOrderAsync(RequestContext ctx, ReceptionOrderBody body)
    {
        ctx.RequireReception();

        var hasId = !string.IsNullOrWhiteSpace(body.PatientId);
        if (hasId == (body.Patient is not null))
        {
            throw ApiException.BadRequest("Phải chọn người bệnh có sẵn hoặc nhập người bệnh mới, không được cả hai");
        }

        Guid? patientId = hasId ? ApiFormat.ParseId(body.PatientId!) : null;
        var patient = body.Patient is null ? null : CheckPatient(body.Patient);
        var serviceIds = await CheckServicesAsync(ctx, body.ServiceIds);

        var order = new NewOrder(
            patientId, patient, serviceIds,
            Clean(body.OrderingDoctor, MaxShortText, "Bác sĩ chỉ định"),
            Clean(body.OrderingDept, MaxShortText, "Khoa/phòng chỉ định"),
            Clean(body.Diagnosis, MaxAddress, "Chẩn đoán"));

        // Phòng khám tạo từ trước khi có tiếp nhận có thể chưa có dòng cơ sở; có rồi thì giữ nguyên.
        await new FacilityStore(ctx.Tenant.Db).EnsureDefaultAsync(ctx.Clinic.Name);

        OrderCreated created;
        try
        {
            created = await ctx.Tenant.Orders.CreateAsync(order, ctx.User.Id);
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }

        return Results.Json(
            new
            {
                orderId = created.OrderId,
                patientId = created.PatientId,
                patientCode = created.PatientCode,
                samples = created.Samples.Select(s => new { sampleId = s.SampleId, barcode = s.Barcode, specimenName = s.SpecimenName, serviceNames = s.ServiceNames }),
            },
            statusCode: StatusCodes.Status201Created);
    }

    private static NewPatient CheckPatient(ReceptionPatientBody body)
    {
        var name = (body.FullName ?? "").Trim();
        if (name.Length == 0)
        {
            throw ApiException.BadRequest("Chưa nhập họ tên người bệnh");
        }

        if (name.Length > MaxNameLength)
        {
            throw ApiException.BadRequest($"Họ tên tối đa {MaxNameLength} ký tự");
        }

        if (body.Dob is null && body.BirthYear is null)
        {
            throw ApiException.BadRequest("Phải nhập ngày sinh hoặc năm sinh");
        }

        var thisYear = DateTime.UtcNow.Year;
        if (body.BirthYear is { } year && (year < MinBirthYear || year > thisYear))
        {
            throw ApiException.BadRequest($"Năm sinh phải từ {MinBirthYear} đến {thisYear}");
        }

        if (body.Dob is { } dob && (dob.Year < MinBirthYear || dob > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))))
        {
            throw ApiException.BadRequest("Ngày sinh không hợp lệ");
        }

        if (body.Sex is not ("M" or "F" or "O"))
        {
            throw ApiException.BadRequest("Giới tính phải là M, F hoặc O");
        }

        return new NewPatient(
            name, body.Dob, body.BirthYear, body.Sex[0],
            Clean(body.Address, MaxAddress, "Địa chỉ"), Clean(body.Phone, 30, "Số điện thoại"));
    }

    private static async Task<List<Guid>> CheckServicesAsync(RequestContext ctx, List<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            throw ApiException.BadRequest("Chưa chọn dịch vụ nào");
        }

        if (raw.Count > MaxServices)
        {
            throw ApiException.BadRequest($"Tối đa {MaxServices} dịch vụ mỗi chỉ định");
        }

        var ids = raw.Select(ApiFormat.ParseId).ToList();
        var services = (await ctx.Tenant.Catalog.ListServicesAsync(activeOnly: false)).ToDictionary(s => s.Id);
        foreach (var id in ids)
        {
            if (!services.TryGetValue(id, out var service))
            {
                throw ApiException.BadRequest("Dịch vụ không tồn tại trong danh mục");
            }

            if (!service.Active)
            {
                throw ApiException.BadRequest($"Dịch vụ '{service.Name}' ({service.Code}) đã ngừng dùng");
            }
        }

        return ids;
    }

    private static string? Clean(string? value, int max, string label)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length > max ? throw ApiException.BadRequest($"{label} tối đa {max} ký tự") : text;
    }
}
