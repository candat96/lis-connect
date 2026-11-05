using System.Globalization;
using Dapper;
using Lis.Core.Patients;
using Lis.Core.Results;

namespace Lis.Data.Results;

/// <summary>Trạng thái mẫu trên màn Kết quả (bộ lọc của <see cref="IResultService.ListAsync"/>).</summary>
public static class SampleStatus
{
    /// <summary>Chưa duyệt, còn chỉ số chưa có giá trị.</summary>
    public const string AwaitingResults = "cho-ket-qua";

    /// <summary>Chưa duyệt, mọi chỉ số đã có giá trị.</summary>
    public const string AwaitingApproval = "cho-duyet";

    /// <summary>Đã duyệt (<c>sample.approved_at</c> có giá trị).</summary>
    public const string Approved = "da-duyet";
}

/// <summary>
/// Phần đọc của màn Kết quả: danh sách mẫu theo ngày/trạng thái/tìm kiếm và bảng chỉ số của
/// một mẫu. Mọi ghi chép nằm ở <see cref="ResultService"/>.
/// </summary>
public sealed class SampleResultsQuery(Db db, TimeProvider clock)
{
    /// <summary>Tên hiển thị khi mẫu được tự duyệt (<c>sample.approved_by</c> NULL).</summary>
    public const string AutoApprovedBy = "Tự duyệt";

    /// <summary>
    /// Danh sách mẫu có chỉ định tạo trong khoảng ngày [<paramref name="from"/>, <paramref name="to"/>]
    /// theo giờ máy (múi giờ của <see cref="TimeProvider.LocalTimeZone"/>), mới nhất trước.
    /// <paramref name="query"/> khớp barcode chính xác (sau khi bỏ khoảng trắng hai đầu), mã
    /// người bệnh, hoặc một phần họ tên không phân biệt dấu/hoa thường.
    /// </summary>
    public async Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query)
    {
        var (fromUtc, toUtcExclusive) = LocalDay.UtcRange(clock, from, to);

        var conditions = new List<string> { "o.created_at >= @fromUtc AND o.created_at < @toUtcExclusive" };

        var trimmedQuery = query?.Trim();
        if (!string.IsNullOrEmpty(trimmedQuery))
        {
            conditions.Add(
                "(s.barcode = @query OR lower(p.code) = lower(@query) OR unaccent(lower(p.full_name)) LIKE unaccent(lower(@pattern)) ESCAPE '\\')");
        }

        switch (status)
        {
            case SampleStatus.Approved:
                conditions.Add("s.approved_at IS NOT NULL");
                break;
            case SampleStatus.AwaitingApproval:
                conditions.Add("s.approved_at IS NULL AND l.total > 0 AND l.with_value = l.total");
                break;
            case SampleStatus.AwaitingResults:
                conditions.Add("s.approved_at IS NULL AND l.with_value < l.total");
                break;
        }

        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<ListRow>(
            $"""
            WITH l AS (
                SELECT oi.sample_id,
                       count(*)::int AS total,
                       count(*) FILTER (WHERE r.value IS NOT NULL AND r.value <> '')::int AS with_value,
                       bool_or(coalesce(r.flag, 'None') <> 'None') AS has_abnormal,
                       bool_or(r.pending_value IS NOT NULL) AS has_pending
                FROM order_item oi
                JOIN service sv ON sv.id = oi.service_id AND sv.is_panel = false
                LEFT JOIN result r ON r.order_item_id = oi.id
                GROUP BY oi.sample_id
            )
            SELECT s.id AS sample_id, s.barcode, p.full_name AS patient_name, o.created_at AS ordered_at,
                   st.name AS specimen_name,
                   coalesce(l.total, 0) AS total, coalesce(l.with_value, 0) AS with_value,
                   s.approved_at IS NOT NULL AS approved,
                   coalesce(l.has_abnormal, false) AS has_abnormal,
                   coalesce(l.has_pending, false) AS has_pending
            FROM sample s
            JOIN lab_order o ON o.id = s.order_id
            JOIN patient p ON p.id = o.patient_id
            JOIN specimen_type st ON st.id = s.specimen_type_id
            LEFT JOIN l ON l.sample_id = s.id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY o.created_at DESC, s.barcode DESC
            """,
            new { fromUtc, toUtcExclusive, query = trimmedQuery, pattern = $"%{EscapeLike(trimmedQuery ?? "")}%" });

        return rows.Select(row => row.ToItem()).ToList();
    }

    /// <summary>Thoát ký tự đại diện của LIKE (<c>%</c>, <c>_</c>, <c>\</c>) để chuỗi người dùng gõ được so theo nghĩa đen.</summary>
    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Bảng chỉ số của một mẫu: dòng cha (panel) rồi các chỉ số con thụt vào, theo
    /// <c>service.print_order</c> của dòng đầu rồi <c>service_child.sort_order</c>.
    /// </summary>
    public async Task<SampleResults> GetAsync(Guid sampleId)
    {
        await using var connection = await db.OpenAsync();

        var sample = await connection.QuerySingleOrDefaultAsync<SampleRow>(
            """
            SELECT s.id AS sample_id, s.barcode, s.approved_at, u.full_name AS approver_name,
                   p.full_name AS patient_name, p.code AS patient_code, p.sex, p.dob, p.birth_year
            FROM sample s
            JOIN lab_order o ON o.id = s.order_id
            JOIN patient p ON p.id = o.patient_id
            LEFT JOIN app_user u ON u.id = s.approved_by
            WHERE s.id = @sampleId
            """,
            new { sampleId })
            ?? throw new InvalidOperationException("Không tìm thấy mẫu");

        var items = (await connection.QueryAsync<ItemRow>(
            """
            SELECT oi.id AS order_item_id, oi.parent_item_id,
                   sv.id AS service_id, sv.code AS service_code, sv.name AS service_name, sv.is_panel, sv.decimals,
                   CASE WHEN oi.parent_item_id IS NULL THEN coalesce(sv.print_order, 0) ELSE coalesce(psv.print_order, 0) END AS top_print_order,
                   coalesce(psv.code, sv.code) AS top_code,
                   coalesce(sc.sort_order, 0) AS child_sort_order,
                   r.id AS result_id, r.value, r.unit, r.ref_low, r.ref_high, r.ref_text, r.flag, r.status,
                   r.pending_value, r.source
            FROM order_item oi
            JOIN service sv ON sv.id = oi.service_id
            LEFT JOIN order_item poi ON poi.id = oi.parent_item_id
            LEFT JOIN service psv ON psv.id = poi.service_id
            LEFT JOIN service_child sc ON sc.parent_id = poi.service_id AND sc.child_id = oi.service_id
            LEFT JOIN result r ON r.order_item_id = oi.id
            WHERE oi.sample_id = @sampleId
            """,
            new { sampleId })).ToList();

        var rows = items
            .OrderBy(i => i.TopPrintOrder)
            .ThenBy(i => i.TopCode, StringComparer.Ordinal)
            .ThenBy(i => i.ParentItemId is null ? 0 : 1)
            .ThenBy(i => i.ChildSortOrder)
            .ThenBy(i => i.ServiceCode, StringComparer.Ordinal)
            .Select(i => i.ToRow())
            .ToList();

        string? approvedBy = sample.ApprovedAt is null ? null : sample.ApproverName ?? AutoApprovedBy;
        var patient = new PatientFacts(sample.Sex[0], sample.Dob, sample.BirthYear);

        return new SampleResults(
            sample.SampleId, sample.Barcode, sample.PatientName, sample.PatientCode, patient,
            sample.ApprovedAt, approvedBy, rows);
    }

    /// <summary>Khoảng tham chiếu để hiển thị: <c>ref_text</c> nếu có, không thì "thấp – cao" theo số lẻ của chỉ số.</summary>
    public static string? FormatRefText(string? refText, decimal? low, decimal? high, int? decimals)
    {
        if (!string.IsNullOrWhiteSpace(refText))
        {
            return refText;
        }

        return (low, high) switch
        {
            (not null, not null) => $"{Format(low.Value)} – {Format(high.Value)}",
            (not null, null) => $"≥ {Format(low.Value)}",
            (null, not null) => $"≤ {Format(high.Value)}",
            _ => null,
        };

        string Format(decimal value) => decimals is { } d
            ? value.ToString("F" + d, CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private sealed record ListRow
    {
        public Guid SampleId { get; init; }
        public string Barcode { get; init; } = "";
        public string PatientName { get; init; } = "";
        public DateTimeOffset OrderedAt { get; init; }
        public string SpecimenName { get; init; } = "";
        public int Total { get; init; }
        public int WithValue { get; init; }
        public bool Approved { get; init; }
        public bool HasAbnormal { get; init; }
        public bool HasPending { get; init; }

        public SampleListItem ToItem() => new(
            SampleId, Barcode, PatientName, OrderedAt, SpecimenName, Total, WithValue, Approved, HasAbnormal, HasPending);
    }

    private sealed record SampleRow
    {
        public Guid SampleId { get; init; }
        public string Barcode { get; init; } = "";
        public DateTimeOffset? ApprovedAt { get; init; }
        public string? ApproverName { get; init; }
        public string PatientName { get; init; } = "";
        public string PatientCode { get; init; } = "";
        public string Sex { get; init; } = "";
        public DateOnly? Dob { get; init; }
        public int? BirthYear { get; init; }
    }

    private sealed record ItemRow
    {
        public Guid OrderItemId { get; init; }
        public Guid? ParentItemId { get; init; }
        public Guid ServiceId { get; init; }
        public string ServiceCode { get; init; } = "";
        public string ServiceName { get; init; } = "";
        public bool IsPanel { get; init; }
        public int? Decimals { get; init; }
        public int TopPrintOrder { get; init; }
        public string TopCode { get; init; } = "";
        public int ChildSortOrder { get; init; }
        public Guid? ResultId { get; init; }
        public string? Value { get; init; }
        public string? Unit { get; init; }
        public decimal? RefLow { get; init; }
        public decimal? RefHigh { get; init; }
        public string? RefText { get; init; }
        public string? Flag { get; init; }
        public string? Status { get; init; }
        public string? PendingValue { get; init; }
        public string? Source { get; init; }

        public ResultRow ToRow() => new(
            OrderItemId,
            ResultId,
            ServiceId,
            ServiceCode,
            ServiceName,
            IsPanelHeader: IsPanel,
            Depth: ParentItemId is null ? 0 : 1,
            Value: IsPanel ? null : Value,
            Unit: IsPanel ? null : Unit,
            RefText: IsPanel ? null : FormatRefText(RefText, RefLow, RefHigh, Decimals),
            // result.flag lưu tên hằng của enum ResultFlag (vd "High"), không lưu số.
            Flag: Flag is null ? ResultFlag.None : Enum.Parse<ResultFlag>(Flag),
            Status,
            PendingValue,
            Source);
    }
}
