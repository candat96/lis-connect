using System.Globalization;
using Lis.Core.Results;
using Lis.Data.Catalog;

namespace Lis.Server.Api;

public sealed record CatalogRangeBody(
    string? Sex, int? AgeFromDays, int? AgeToDays, string? InstrumentId,
    decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh, string? TextRange);

public sealed record CatalogRuleBody(string? InstrumentId, string? RawValue, string? DisplayValue, string? Flag);

public sealed record ServiceBody(
    string? Code, string? Name, string? GroupName, string? SpecimenTypeId, string? Unit, string? ValueType,
    int? Decimals, int? PrintOrder, bool? IsPanel, bool? AutoApprove, bool? Active, string? HisCode,
    string[]? ChildIds, CatalogRangeBody[]? Ranges, CatalogRuleBody[]? QualitativeRules);

/// <summary>Một dịch vụ / gói trong cây danh mục: chỉ số con của gói nằm trong <see cref="Children"/>.</summary>
public sealed record CatalogServiceView(
    Guid Id, string Code, string Name, string? GroupName, string? SpecimenName, string? Unit, string ValueType, int? Decimals,
    bool IsPanel, bool AutoApprove, bool Active, string? RangeText, List<CatalogServiceView> Children);

/// <summary>
/// Danh mục dịch vụ / chỉ số (<c>/api/catalog</c>): ai đăng nhập cũng xem được; chỉ Quản trị thêm / sửa / xoá.
/// Quy tắc (kiểm khoảng tham chiếu, chỉ số con, xoá dịch vụ đang dùng) nằm ở <c>ReferenceRangeValidator</c> và <see cref="CatalogStore"/>.
/// Kiểm đầu vào chạy trước kiểm quyền, giống pipe chạy trước handler ở NestJS.
/// </summary>
public static class CatalogEndpoints
{
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private static readonly string[] ValueTypes = ["so", "dinh-tinh", "chu"];
    private static readonly string[] Flags = ["Low", "High", "CriticalLow", "CriticalHigh", "Abnormal"];
    private static readonly CultureInfo Vietnamese = new("vi");

    public static void MapCatalogApi(this RouteGroupBuilder api)
    {
        var catalog = api.MapGroup("/catalog").RequireSession();
        // Lambda chỉ nhận HttpContext sẽ bị hiểu thành RequestDelegate (bỏ giá trị trả về) — ép về Delegate để kết quả được ghi ra.
        catalog.MapGet("/services", (Delegate)((HttpContext http) => ListAsync(http.Ctx())));
        catalog.MapGet("/meta", (Delegate)((HttpContext http) => MetaAsync(http.Ctx())));
        catalog.MapGet("/services/{id}", (HttpContext http, string id) => DetailAsync(http.Ctx(), id));
        catalog.MapPost("/services", (HttpContext http, ServiceBody body) => CreateAsync(http.Ctx(), body));
        catalog.MapPut("/services/{id}", (HttpContext http, string id, ServiceBody body) => UpdateAsync(http.Ctx(), id, body));
        catalog.MapDelete("/services/{id}", (HttpContext http, string id) => DeleteAsync(http.Ctx(), id));
    }

    /// <summary>Cây dịch vụ: dịch vụ gốc (kèm chỉ số con nếu là gói), theo nhóm rồi mã. Chỉ số con không lặp lại ở gốc.</summary>
    private static async Task<IResult> ListAsync(RequestContext ctx)
    {
        var services = await ctx.Tenant.Catalog.ListServicesAsync(activeOnly: false);
        var specimens = (await ctx.Tenant.Catalog.SpecimenTypesAsync()).ToDictionary(s => s.Id, s => s.Name);
        var rangesByService = (await ctx.Tenant.Catalog.AllRangesAsync()).ToLookup(r => r.ServiceId);

        var views = services.ToDictionary(s => s.Id, s => new CatalogServiceView(
            s.Id, s.Code, s.Name, s.GroupName,
            specimens.TryGetValue(s.SpecimenTypeId, out var specimenName) ? specimenName : null,
            s.Unit, s.ValueType, s.Decimals, s.IsPanel, s.AutoApprove, s.Active,
            RangeText.Summarize([.. rangesByService[s.Id]], s.Decimals), []));

        var childIds = new HashSet<Guid>();
        foreach (var parent in services)
        {
            foreach (var childId in parent.ChildIds)
            {
                if (views.TryGetValue(childId, out var child))
                {
                    views[parent.Id].Children.Add(child);
                    childIds.Add(childId);
                }
            }
        }

        var comparer = Vietnamese.CompareInfo;
        var roots = views.Values.Where(v => !childIds.Contains(v.Id)).ToList();
        roots.Sort((a, b) =>
        {
            var byGroup = comparer.Compare(a.GroupName ?? "", b.GroupName ?? "", CompareOptions.None);
            return byGroup != 0 ? byGroup : comparer.Compare(a.Code, b.Code, CompareOptions.None);
        });
        return Results.Ok(roots);
    }

    private static async Task<IResult> MetaAsync(RequestContext ctx) => Results.Ok(await ctx.Tenant.Catalog.MetaAsync());

    private static async Task<IResult> DetailAsync(RequestContext ctx, string id)
    {
        var serviceId = ApiFormat.ParseId(id);
        return Results.Ok(await ctx.Tenant.Catalog.DetailAsync(serviceId) ?? throw ApiException.NotFound("Không tìm thấy dịch vụ"));
    }

    private static async Task<IResult> CreateAsync(RequestContext ctx, ServiceBody body)
    {
        var input = Check(body);
        ctx.RequireAdmin();
        var id = await SaveAsync(ctx, null, input);
        return Results.Json(new { id }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(RequestContext ctx, string id, ServiceBody body)
    {
        var serviceId = ApiFormat.ParseId(id);
        var input = Check(body);
        ctx.RequireAdmin();
        await SaveAsync(ctx, serviceId, input);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(RequestContext ctx, string id)
    {
        var serviceId = ApiFormat.ParseId(id);
        ctx.RequireAdmin();
        try
        {
            await ctx.Tenant.Catalog.DeleteServiceAsync(serviceId);
        }
        catch (CatalogException ex)
        {
            throw Translate(ex);
        }

        return Results.NoContent();
    }

    private sealed record ServiceInput(ServiceInfo Service, List<ReferenceRange> Ranges, List<QualitativeRule> Rules);

    private static async Task<Guid> SaveAsync(RequestContext ctx, Guid? id, ServiceInput input)
    {
        var problems = ValidateRanges(input.Ranges);
        if (problems.Count > 0) throw ApiException.BadRequest(string.Join("; ", problems));

        var duplicate = input.Rules
            .GroupBy(r => $"{r.InstrumentId}|{r.RawValue.Trim().ToLowerInvariant()}")
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw ApiException.BadRequest($"Luật định tính bị lặp giá trị máy gửi \"{duplicate.Key.Split('|', 2)[1]}\"");
        }

        try
        {
            return await ctx.Tenant.Catalog.SaveWithDetailsAsync(id, input.Service, input.Ranges, input.Rules);
        }
        catch (CatalogException ex)
        {
            throw Translate(ex);
        }
    }

    private static ApiException Translate(CatalogException ex) => ex.Failure switch
    {
        CatalogFailure.NotFound => ApiException.NotFound(ex.Message),
        CatalogFailure.DuplicateCode or CatalogFailure.InUse => ApiException.Conflict(ex.Message),
        _ => ApiException.BadRequest(ex.Message),
    };

    /// <summary>
    /// Thấp ≤ cao; nguy hiểm thấp ≤ thấp; cao ≤ nguy hiểm cao; tuổi từ ≤ tuổi đến — quy tắc của <see cref="ReferenceRangeValidator"/>
    /// (chạy từng dòng để giữ nhãn "Khoảng tham chiếu dòng N" của web); thêm: mỗi dòng phải có ít nhất một giá trị hoặc chữ.
    /// </summary>
    private static List<string> ValidateRanges(List<ReferenceRange> ranges)
    {
        var errors = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var row = $"Khoảng tham chiếu dòng {i + 1}";
            errors.AddRange(ReferenceRangeValidator.Validate([ranges[i]]).Select(e => row + e["Dòng 1".Length..]));
            var r = ranges[i];
            if (r.Low is null && r.High is null && r.CriticalLow is null && r.CriticalHigh is null && string.IsNullOrWhiteSpace(r.TextRange))
            {
                errors.Add($"{row}: nhập ít nhất một giá trị hoặc chữ tham chiếu");
            }
        }

        return errors;
    }

    /// <summary>Kiểm thân request như DTO ở NestJS (cùng câu báo lỗi); lỗi không có câu riêng → "dữ liệu không hợp lệ".</summary>
    private static ServiceInput Check(ServiceBody body)
    {
        var code = body.Code ?? throw ApiException.BadRequest(InvalidBody);
        if (code.Length == 0) throw ApiException.BadRequest("Nhập mã dịch vụ");
        if (code.Length > 64) throw ApiException.BadRequest(InvalidBody);
        if (code.Any(char.IsWhiteSpace)) throw ApiException.BadRequest("Mã dịch vụ không được có khoảng trắng");

        var name = body.Name ?? throw ApiException.BadRequest(InvalidBody);
        if (name.Length == 0) throw ApiException.BadRequest("Nhập tên dịch vụ");
        if (name.Length > 500) throw ApiException.BadRequest(InvalidBody);

        Limit(body.GroupName, 100);
        var specimenTypeId = OptionalId(body.SpecimenTypeId, "Chọn loại mẫu") ?? throw ApiException.BadRequest("Chọn loại mẫu");
        Limit(body.Unit, 50);
        if (body.ValueType is null || !ValueTypes.Contains(body.ValueType)) throw ApiException.BadRequest("Kiểu kết quả không hợp lệ");
        if (body.Decimals is < 0 or > 6) throw ApiException.BadRequest(InvalidBody);
        if (body.PrintOrder is < 0 or > 100000) throw ApiException.BadRequest(InvalidBody);
        var isPanel = body.IsPanel ?? throw ApiException.BadRequest(InvalidBody);
        var autoApprove = body.AutoApprove ?? throw ApiException.BadRequest(InvalidBody);
        var active = body.Active ?? throw ApiException.BadRequest(InvalidBody);
        Limit(body.HisCode, 64);
        if (body.ChildIds is not { Length: <= 300 }) throw ApiException.BadRequest(InvalidBody);
        var childIds = body.ChildIds.Select(c => OptionalId(c, InvalidBody) ?? throw ApiException.BadRequest(InvalidBody)).ToList();
        if (body.Ranges is not { Length: <= 100 }) throw ApiException.BadRequest(InvalidBody);
        var ranges = body.Ranges.Select(CheckRange).ToList();
        if (body.QualitativeRules is not { Length: <= 100 }) throw ApiException.BadRequest(InvalidBody);
        var rules = body.QualitativeRules.Select(CheckRule).ToList();

        // Gói không có đơn vị / số lẻ / tự duyệt riêng.
        var service = new ServiceInfo(
            Guid.Empty, code.Trim(), name.Trim(), Trimmed(body.GroupName), specimenTypeId,
            isPanel ? null : Trimmed(body.Unit), body.ValueType, isPanel ? null : body.Decimals, body.PrintOrder ?? 0,
            isPanel, !isPanel && autoApprove, active, Trimmed(body.HisCode), childIds);
        return new ServiceInput(service, ranges, rules);
    }

    private static ReferenceRange CheckRange(CatalogRangeBody r)
    {
        if (r.Sex is not (null or "M" or "F")) throw ApiException.BadRequest("Giới tính không hợp lệ");
        if (r.AgeFromDays is < 0 or > 60000 || r.AgeToDays is < 0 or > 60000) throw ApiException.BadRequest(InvalidBody);
        var instrumentId = OptionalId(r.InstrumentId, InvalidBody);
        if (r.TextRange is { Length: > 200 }) throw ApiException.BadRequest(InvalidBody);
        return new ReferenceRange(
            Guid.Empty, Guid.Empty, r.Sex is null ? null : r.Sex[0], r.AgeFromDays, r.AgeToDays, instrumentId,
            r.Low, r.High, r.CriticalLow, r.CriticalHigh, Trimmed(r.TextRange));
    }

    private static QualitativeRule CheckRule(CatalogRuleBody r)
    {
        var instrumentId = OptionalId(r.InstrumentId, InvalidBody);
        var raw = r.RawValue ?? throw ApiException.BadRequest(InvalidBody);
        if (raw.Length == 0) throw ApiException.BadRequest("Nhập giá trị máy gửi");
        if (raw.Length > 100) throw ApiException.BadRequest(InvalidBody);
        var display = r.DisplayValue ?? throw ApiException.BadRequest(InvalidBody);
        if (display.Length == 0) throw ApiException.BadRequest("Nhập chữ hiển thị");
        if (display.Length > 100) throw ApiException.BadRequest(InvalidBody);
        if (r.Flag is not null && !Flags.Contains(r.Flag)) throw ApiException.BadRequest("Cờ không hợp lệ");
        return new QualitativeRule(instrumentId, Guid.Empty, raw.Trim(), display.Trim(), r.Flag is null ? null : Enum.Parse<ResultFlag>(r.Flag));
    }

    private static void Limit(string? value, int max)
    {
        if (value is not null && value.Length > max) throw ApiException.BadRequest(InvalidBody);
    }

    private static Guid? OptionalId(string? value, string message) =>
        value is null ? null : Guid.TryParseExact(value, "D", out var id) ? id : throw ApiException.BadRequest(message);

    /// <summary>Cắt khoảng trắng hai đầu; rỗng → null (như <c>x?.trim() || null</c> ở NestJS).</summary>
    private static string? Trimmed(string? value) => string.IsNullOrEmpty(value?.Trim()) ? null : value.Trim();
}

/// <summary>Khoảng tham chiếu gộp để xem nhanh (vd "Nam: 130 – 170 · Nữ: 120 – 150").</summary>
internal static class RangeText
{
    /// <summary>Chỉ gộp khoảng chung (không theo máy, không theo tuổi); không có thì "Theo tuổi / máy" nếu còn khoảng khác, hoặc null.</summary>
    public static string? Summarize(IReadOnlyList<ReferenceRange> ranges, int? decimals)
    {
        var parts = ranges
            .Where(r => r.InstrumentId is null && r.AgeFromDays is null && r.AgeToDays is null)
            .Select(r => Format(r.TextRange, r.Low, r.High, decimals) is { } text
                ? r.Sex switch { 'M' => $"Nam: {text}", 'F' => $"Nữ: {text}", _ => text }
                : null)
            .OfType<string>()
            .ToList();
        if (parts.Count > 0) return string.Join(" · ", parts);
        return ranges.Count > 0 ? "Theo tuổi / máy" : null;
    }

    /// <summary>Chữ nếu có, không thì "thấp – cao" / "≥ thấp" / "≤ cao"; số lẻ theo chỉ số (không có thì tối đa 4 chữ số, bỏ số 0 thừa).</summary>
    public static string? Format(string? text, decimal? low, decimal? high, int? decimals)
    {
        if (!string.IsNullOrWhiteSpace(text)) return text;
        string Number(decimal value) => decimals is { } d
            ? ValueRounding.Round(value.ToString(CultureInfo.InvariantCulture), d)
            : Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
        return (low, high) switch
        {
            ({ } l, { } h) => $"{Number(l)} – {Number(h)}",
            ({ } l, null) => $"≥ {Number(l)}",
            (null, { } h) => $"≤ {Number(h)}",
            _ => null,
        };
    }
}
