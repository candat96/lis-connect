using System.Globalization;
using Lis.Data.Results;

namespace Lis.Server.Api;

public sealed record SetValueBody(string? Value, string? Reason);

public sealed record ReasonBody(string? Reason);

public sealed record SampleListItemView(
    Guid SampleId, string Barcode, string PatientName, string PatientCode, string OrderedAt, string SpecimenName,
    int Total, int WithValue, string Status, bool HasAbnormal, bool HasPending);

public sealed record SamplePatientView(string Name, string Code, string Sex, string? Dob, int? BirthYear);

public sealed record SampleOrderView(string? Doctor, string? Dept, string? Diagnosis, string OrderedAt);

public sealed record ResultRowView(
    Guid OrderItemId, Guid? ResultId, string ValueType, IReadOnlyList<string> Choices, string ServiceCode, string ServiceName,
    bool IsPanelHeader, int Depth, string? Value, string? Unit, string? RefText, string Flag, string? Status, string? PendingValue,
    string? Source, string? PendingInstrumentFlag, string? PendingReason, string? PendingAt);

public sealed record SampleDetailView(
    Guid SampleId, string Barcode, SamplePatientView Patient, SampleOrderView Order, string? ApprovedAt, string? ApprovedBy,
    IReadOnlyList<ResultRowView> Rows);

public sealed record ResultHistoryView(string At, string Action, string? OldValue, string? NewValue, string? Reason, string? ActorName);

/// <summary>
/// Màn Kết quả (<c>/api/samples</c>, <c>/api/results</c>): ai đăng nhập cũng xem được danh sách, chi tiết, lịch sử; nhập / sửa /
/// duyệt / chấp nhận / bỏ bản máy gửi lại cần Quản trị, Kỹ thuật viên hoặc Trưởng khoa; hủy duyệt chỉ Quản trị, Trưởng khoa.
/// Mọi quy tắc ghi (làm tròn, cờ, khoảng tham chiếu, bắt buộc lý do, khóa mẫu, lịch sử) nằm ở <see cref="ResultService"/> —
/// ở đây chỉ kiểm đầu vào như DTO của NestJS, kiểm quyền, gọi service với người thao tác là tài khoản của phiên, và dịch lỗi
/// sang mã HTTP. Thứ tự: đọc tham số → kiểm thân → kiểm quyền → nghiệp vụ.
/// </summary>
public static class SamplesEndpoints
{
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private const string InvalidDateRange = "Khoảng ngày không hợp lệ (from, to dạng yyyy-MM-dd)";
    private const int MaxTextLength = 500;

    /// <summary>Số mẫu tối đa trả về một lần (như <c>LIMIT 500</c> ở NestJS); danh sách đã xếp mới nhất trước.</summary>
    private const int MaxListItems = 500;

    private static readonly ServiceEntryHint FreeTextHint = new("chu", []);

    private static readonly string[] Statuses = [SampleStatus.AwaitingResults, SampleStatus.AwaitingApproval, SampleStatus.Approved];

    public static void MapSamplesApi(this RouteGroupBuilder api)
    {
        var samples = api.MapGroup("/samples").RequireSession();
        // Ép về Delegate: lambda chỉ nhận HttpContext sẽ bị hiểu là RequestDelegate và bỏ mất kết quả trả về.
        samples.MapGet("", (Delegate)((HttpContext http) => ListAsync(http.Ctx(), http.Request.Query)));
        samples.MapGet("/{id}", (HttpContext http, string id) => DetailAsync(http.Ctx(), id));
        samples.MapPost("/{id}/approve", (HttpContext http, string id) => ApproveAsync(http.Ctx(), id));
        samples.MapPost("/{id}/unapprove", (HttpContext http, string id, ReasonBody body) => UnapproveAsync(http.Ctx(), id, body));

        var results = api.MapGroup("/results").RequireSession();
        results.MapPut("/items/{orderItemId}", (HttpContext http, string orderItemId, SetValueBody body) => SetValueAsync(http.Ctx(), orderItemId, body));
        results.MapGet("/items/{orderItemId}/history", (HttpContext http, string orderItemId) => HistoryAsync(http.Ctx(), orderItemId));
        results.MapPost("/{resultId}/pending/accept", (HttpContext http, string resultId) => AcceptPendingAsync(http.Ctx(), resultId));
        results.MapPost("/{resultId}/pending/reject", (HttpContext http, string resultId) => RejectPendingAsync(http.Ctx(), resultId));
    }

    /// <summary>Mẫu có chỉ định tạo trong khoảng ngày [from, to] theo giờ phòng khám, lọc trạng thái và ô tìm, mới nhất trước.</summary>
    private static async Task<IResult> ListAsync(RequestContext ctx, IQueryCollection query)
    {
        if (!TryParseDate(query["from"], out var from) || !TryParseDate(query["to"], out var to) || from > to)
        {
            throw ApiException.BadRequest(InvalidDateRange);
        }

        string? status = query["status"];
        if (string.IsNullOrEmpty(status))
        {
            status = null;
        }
        else if (!Statuses.Contains(status))
        {
            throw ApiException.BadRequest("Trạng thái không hợp lệ");
        }

        var items = await ctx.Tenant.Results.ListAsync(from, to, status, query["q"], MaxListItems);
        return Results.Ok(items.Select(item => new SampleListItemView(
            item.SampleId, item.Barcode, item.PatientName, item.PatientCode, ApiFormat.Iso(item.OrderedAt), item.SpecimenName,
            item.Total, item.WithValue, StatusOf(item), item.HasAbnormal, item.HasPending)));
    }

    /// <summary>Bảng chỉ số của một mẫu kèm người bệnh, phiếu chỉ định và trạng thái duyệt.</summary>
    private static async Task<IResult> DetailAsync(RequestContext ctx, string id)
    {
        var sampleId = ApiFormat.ParseId(id);
        var results = ctx.Tenant.Results;
        var (sample, hints, header) = await Translated(async () =>
            (await results.GetAsync(sampleId), await results.GetEntryHintsAsync(sampleId), await results.GetOrderHeaderAsync(sampleId)));

        var rows = sample.Rows.Select(row =>
        {
            // Hai truy vấn đọc riêng: chỉ định thêm vào giữa chừng thì chưa có gợi ý — cho nhập chữ tự do thay vì 500.
            var hint = hints.GetValueOrDefault(row.ServiceId) ?? FreeTextHint;
            return new ResultRowView(
                row.OrderItemId, row.ResultId, hint.ValueType, hint.Choices, row.ServiceCode, row.ServiceName, row.IsPanelHeader, row.Depth,
                row.Value, row.Unit, row.RefText, row.Flag.ToString(), row.Status, row.PendingValue, row.Source,
                row.PendingInstrumentFlag, row.PendingReason, row.PendingAt is { } pendingAt ? ApiFormat.Iso(pendingAt) : null);
        }).ToList();

        var patient = sample.Patient;
        return Results.Ok(new SampleDetailView(
            sample.SampleId,
            sample.Barcode,
            new SamplePatientView(
                sample.PatientName, sample.PatientCode, patient.Sex.ToString(),
                patient.Dob?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), patient.BirthYear),
            new SampleOrderView(header.OrderingDoctor, header.OrderingDept, header.Diagnosis, ApiFormat.Iso(header.OrderedAt)),
            sample.ApprovedAt is { } approvedAt ? ApiFormat.Iso(approvedAt) : null,
            sample.ApprovedBy,
            rows));
    }

    /// <summary>Duyệt mẫu: mọi kết quả đang có giá trị thành đã duyệt. Mẫu đã duyệt rồi thì không làm gì (vẫn 204).</summary>
    private static async Task<IResult> ApproveAsync(RequestContext ctx, string id)
    {
        var sampleId = ApiFormat.ParseId(id);
        ctx.RequireResultEditor();
        await Translated(() => ctx.Tenant.Results.ApproveAsync(sampleId, ctx.User.Id));
        return Results.NoContent();
    }

    /// <summary>Hủy duyệt (bắt buộc lý do). Mẫu chưa duyệt thì không làm gì (vẫn 204).</summary>
    private static async Task<IResult> UnapproveAsync(RequestContext ctx, string id, ReasonBody body)
    {
        var sampleId = ApiFormat.ParseId(id);
        var reason = body.Reason ?? throw ApiException.BadRequest(InvalidBody);
        if (reason.Length == 0) throw ApiException.BadRequest("Nhập lý do");
        if (reason.Length > MaxTextLength) throw ApiException.BadRequest(InvalidBody);
        ctx.RequireUnapprover();
        await Translated(() => ctx.Tenant.Results.UnapproveAsync(sampleId, ctx.User.Id, reason));
        return Results.NoContent();
    }

    /// <summary>Nhập / sửa tay một giá trị. Ô đã có giá trị thì phải kèm lý do; mẫu đã duyệt thì phải hủy duyệt trước.</summary>
    private static async Task<IResult> SetValueAsync(RequestContext ctx, string orderItemId, SetValueBody body)
    {
        var itemId = ApiFormat.ParseId(orderItemId);
        var value = body.Value ?? throw ApiException.BadRequest(InvalidBody);
        if (value.Length > MaxTextLength || body.Reason is { Length: > MaxTextLength }) throw ApiException.BadRequest(InvalidBody);
        ctx.RequireResultEditor();
        await Translated(() => ctx.Tenant.Results.SetManualAsync(itemId, value, ctx.User.Id, body.Reason));
        return Results.NoContent();
    }

    /// <summary>Lịch sử thao tác trên một chỉ số của mẫu, mới nhất trước.</summary>
    private static async Task<IResult> HistoryAsync(RequestContext ctx, string orderItemId)
    {
        var itemId = ApiFormat.ParseId(orderItemId);
        var history = await ctx.Tenant.Results.GetItemHistoryAsync(itemId);
        return Results.Ok(history.Select(h => new ResultHistoryView(ApiFormat.Iso(h.At), h.Action, h.OldValue, h.NewValue, h.Reason, h.ActorName)));
    }

    /// <summary>Dùng giá trị máy gửi lại làm giá trị chính thức; mẫu đang duyệt thì về chưa duyệt.</summary>
    private static async Task<IResult> AcceptPendingAsync(RequestContext ctx, string resultId)
    {
        var id = ApiFormat.ParseId(resultId);
        ctx.RequireResultEditor();
        await Translated(() => ctx.Tenant.Results.AcceptPendingAsync(id, ctx.User.Id));
        return Results.NoContent();
    }

    /// <summary>Bỏ giá trị máy gửi lại, giữ giá trị đã duyệt.</summary>
    private static async Task<IResult> RejectPendingAsync(RequestContext ctx, string resultId)
    {
        var id = ApiFormat.ParseId(resultId);
        ctx.RequireResultEditor();
        await Translated(() => ctx.Tenant.Results.RejectPendingAsync(id, ctx.User.Id));
        return Results.NoContent();
    }

    /// <summary>Trạng thái hiển thị: đã duyệt; chờ duyệt khi mọi chỉ số đã có giá trị; còn lại chờ kết quả (cùng quy tắc với bộ lọc của <see cref="SampleResultsQuery"/>).</summary>
    private static string StatusOf(SampleListItem item) =>
        item.Approved ? SampleStatus.Approved
        : item.Total > 0 && item.WithValue == item.Total ? SampleStatus.AwaitingApproval
        : SampleStatus.AwaitingResults;

    private static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static async Task Translated(Func<Task> action) => await Translated(async () =>
    {
        await action();
        return true;
    });

    /// <summary>Lỗi nghiệp vụ của <see cref="ResultService"/> → mã HTTP; câu báo lỗi giữ nguyên của Lis.Data.</summary>
    private static async Task<T> Translated<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (ResultException ex)
        {
            throw ex.Failure switch
            {
                ResultFailure.NotFound => ApiException.NotFound(ex.Message),
                ResultFailure.Conflict => ApiException.Conflict(ex.Message),
                _ => ApiException.BadRequest(ex.Message),
            };
        }
        catch (ResultInputException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }
}
