using Dapper;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Catalog;
using Npgsql;

namespace Lis.Data.Results;

/// <summary>
/// Một dòng trên bảng chỉ số của một mẫu. Dòng cha (panel) có <see cref="IsPanelHeader"/> true,
/// không có giá trị; chỉ số con có <see cref="Depth"/> 1. <see cref="ResultId"/>/<see cref="Status"/>
/// null khi chỉ số chưa có kết quả nào.
/// </summary>
public sealed record ResultRow(
    Guid OrderItemId,
    Guid? ResultId,
    Guid ServiceId,
    string ServiceCode,
    string ServiceName,
    bool IsPanelHeader,
    int Depth,
    string? Value,
    string? Unit,
    string? RefText,
    ResultFlag Flag,
    string? Status,
    string? PendingValue,
    string? Source);

/// <summary>Một mẫu kèm người bệnh, trạng thái duyệt và bảng chỉ số. <see cref="ApprovedBy"/> là "Tự duyệt" khi mẫu được tự duyệt.</summary>
public sealed record SampleResults(
    Guid SampleId,
    string Barcode,
    string PatientName,
    string PatientCode,
    PatientFacts Patient,
    DateTimeOffset? ApprovedAt,
    string? ApprovedBy,
    IReadOnlyList<ResultRow> Rows);

/// <summary>Một dòng trong danh sách mẫu của màn Kết quả. <see cref="Total"/>/<see cref="WithValue"/> đếm chỉ số lá (không tính dòng panel).</summary>
public sealed record SampleListItem(
    Guid SampleId,
    string Barcode,
    string PatientName,
    DateTimeOffset OrderedAt,
    string SpecimenName,
    int Total,
    int WithValue,
    bool Approved,
    bool HasAbnormal,
    bool HasPending);

/// <summary>
/// Nghiệp vụ kết quả mà màn Kết quả cần. Cài đặt thật là <see cref="ResultService"/>; kiểm thử
/// ViewModel dùng bản giả.
/// </summary>
public interface IResultService
{
    /// <summary><paramref name="status"/> nhận <see cref="SampleStatus"/> hoặc null (mọi trạng thái).</summary>
    Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query);

    Task<SampleResults> GetAsync(Guid sampleId);

    /// <summary>Nhập tay một giá trị. Ô đã có giá trị thì bắt buộc <paramref name="reason"/>.</summary>
    Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason);

    Task ApproveAsync(Guid sampleId, Guid actorId);

    Task UnapproveAsync(Guid sampleId, Guid actorId, string reason);

    /// <summary>Dùng bản máy gửi lại (<c>pending_value</c>) làm giá trị chính thức và đưa mẫu về chưa duyệt.</summary>
    Task AcceptPendingAsync(Guid resultId, Guid actorId);

    Task RejectPendingAsync(Guid resultId, Guid actorId);

    /// <summary>Tự duyệt khi mọi chỉ số trên mẫu cho phép, đủ giá trị, cờ đều None và máy không gắn cờ.</summary>
    Task<bool> TryAutoApproveAsync(Guid sampleId);
}

/// <summary>
/// Ghi kết quả xét nghiệm (bảng <c>result</c>) và lịch sử (<c>result_history</c>). Mỗi lần ghi
/// giá trị đều qua một đường duy nhất (<see cref="ComputeAsync"/>): làm tròn theo số lẻ của
/// chỉ số, chọn khoảng tham chiếu theo người bệnh / ngày chỉ định / máy, tính cờ, và lưu
/// khoảng đã áp vào <c>result</c>. Mọi thao tác ghi nằm trong một giao dịch.
/// </summary>
public sealed class ResultService(Db db, TimeProvider clock) : IResultService
{
    private const string StatusPending = "cho-duyet";
    private const string StatusApproved = "da-duyet";

    private const string SourceManual = "tay";
    private const string SourceInstrument = "may";

    private readonly CatalogStore _catalog = new(db);
    private readonly SampleResultsQuery _query = new(db, clock);

    public Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query) =>
        _query.ListAsync(from, to, status, query);

    public Task<SampleResults> GetAsync(Guid sampleId) => _query.GetAsync(sampleId);

    public async Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var item = await connection.QuerySingleOrDefaultAsync<ItemContext>(
                $"""
                {ItemContextSelect("LEFT JOIN")}
                WHERE oi.id = @orderItemId
                FOR UPDATE OF oi, s
                """,
                new { orderItemId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng chỉ định");

            if (item.ApprovedAt is not null)
            {
                throw new InvalidOperationException("Mẫu đã duyệt — hủy duyệt trước khi sửa");
            }

            var service = await LoadServiceAsync(connection, transaction, item.ServiceId);
            if (service.IsPanel)
            {
                throw new InvalidOperationException("Dịch vụ cha (panel) không có giá trị kết quả");
            }

            // Đọc lại kết quả hiện có *sau khi* đã giữ khóa mẫu: bản LEFT JOIN ở trên có thể cũ
            // (READ COMMITTED) khi một máy trạm khác vừa ghi xong cùng ô — nếu dựa vào đó,
            // người thứ hai ghi "nhan" không lý do và đè mất giá trị người thứ nhất.
            var existing = await connection.QuerySingleOrDefaultAsync<ExistingResultRow>(
                "SELECT id, value FROM result WHERE order_item_id = @orderItemId FOR UPDATE",
                new { orderItemId }, transaction);
            var oldValue = existing?.Value;

            var hasOldValue = !string.IsNullOrEmpty(oldValue);
            if (hasOldValue && string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Nhập lý do sửa kết quả");
            }

            var computed = await ComputeAsync(service, item.Patient, item.OrderedLocalDate(clock), instrumentId: null, value);
            var now = clock.GetUtcNow();

            var resultId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO result
                    (order_item_id, value, unit, ref_low, ref_high, ref_text, flag, source, instrument_id, raw_ref,
                     instrument_flag, status, updated_at, updated_by)
                VALUES
                    (@orderItemId, @value, @unit, @refLow, @refHigh, @refText, @flag, @source, NULL, NULL,
                     NULL, @status, @now, @actorId)
                ON CONFLICT (order_item_id) DO UPDATE SET
                    value = EXCLUDED.value, unit = EXCLUDED.unit,
                    ref_low = EXCLUDED.ref_low, ref_high = EXCLUDED.ref_high, ref_text = EXCLUDED.ref_text,
                    flag = EXCLUDED.flag, source = EXCLUDED.source, instrument_id = NULL, raw_ref = NULL,
                    instrument_flag = NULL, status = EXCLUDED.status,
                    updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
                RETURNING id
                """,
                new
                {
                    orderItemId,
                    value = computed.Value,
                    unit = computed.Unit,
                    refLow = computed.RefLow,
                    refHigh = computed.RefHigh,
                    refText = computed.RefText,
                    flag = computed.Flag.ToString(),
                    source = SourceManual,
                    status = StatusPending,
                    now,
                    actorId,
                },
                transaction);

            await InsertHistoryAsync(
                connection, transaction, resultId,
                hasOldValue ? "sua" : "nhan",
                oldValue, newValue: computed.Value,
                reason: hasOldValue ? reason!.Trim() : null,
                actorId, now);

            return true;
        });
    }

    public async Task ApproveAsync(Guid sampleId, Guid actorId)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var approvedAt = await LockSampleAsync(connection, transaction, sampleId);
            if (approvedAt is not null)
            {
                return false;
            }

            var withValue = await connection.ExecuteScalarAsync<int>(
                $"SELECT count(*)::int FROM result r {JoinResultToSample} AND r.value IS NOT NULL AND r.value <> ''",
                new { sampleId }, transaction);

            if (withValue == 0)
            {
                throw new InvalidOperationException("Chưa có kết quả để duyệt");
            }

            await ApproveLockedSampleAsync(connection, transaction, sampleId, actorId, clock.GetUtcNow());
            return true;
        });
    }

    public async Task UnapproveAsync(Guid sampleId, Guid actorId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Nhập lý do hủy duyệt");
        }

        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var approvedAt = await LockSampleAsync(connection, transaction, sampleId);
            if (approvedAt is null)
            {
                return false;
            }

            await UnapproveLockedSampleAsync(connection, transaction, sampleId, actorId, reason.Trim(), clock.GetUtcNow());
            return true;
        });
    }

    public async Task AcceptPendingAsync(Guid resultId, Guid actorId)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var item = await LockResultAsync(connection, transaction, resultId);
            if (item.PendingValue is null)
            {
                throw new InvalidOperationException("Không có bản thay thế để chấp nhận");
            }

            var service = await LoadServiceAsync(connection, transaction, item.ServiceId);
            var computed = await ComputeAsync(service, item.Patient, item.OrderedLocalDate(clock), item.PendingInstrumentId, item.PendingValue);
            var now = clock.GetUtcNow();

            // Cờ máy gửi kèm bản thay thế không được lưu (không có cột pending_instrument_flag),
            // nên instrument_flag về NULL — cờ cũ thuộc về giá trị cũ, không giữ lại.
            await connection.ExecuteAsync(
                """
                UPDATE result SET
                    value = @value, unit = @unit, ref_low = @refLow, ref_high = @refHigh, ref_text = @refText,
                    flag = @flag, source = @source, instrument_id = pending_instrument_id, raw_ref = pending_raw_ref,
                    instrument_flag = NULL, status = @status,
                    pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL,
                    updated_at = @now, updated_by = @actorId
                WHERE id = @resultId
                """,
                new
                {
                    resultId,
                    value = computed.Value,
                    unit = computed.Unit,
                    refLow = computed.RefLow,
                    refHigh = computed.RefHigh,
                    refText = computed.RefText,
                    flag = computed.Flag.ToString(),
                    source = SourceInstrument,
                    status = StatusPending,
                    now,
                    actorId,
                },
                transaction);

            await InsertHistoryAsync(connection, transaction, resultId, "thay-the", item.Value, item.PendingValue, reason: null, actorId, now);

            if (item.ApprovedAt is not null)
            {
                await UnapproveLockedSampleAsync(connection, transaction, item.SampleId, actorId, "Chấp nhận bản thay thế của máy", now);
            }

            return true;
        });
    }

    public async Task RejectPendingAsync(Guid resultId, Guid actorId)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var item = await LockResultAsync(connection, transaction, resultId);
            if (item.PendingValue is null)
            {
                throw new InvalidOperationException("Không có bản thay thế để bỏ");
            }

            await connection.ExecuteAsync(
                "UPDATE result SET pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL WHERE id = @resultId",
                new { resultId }, transaction);

            await InsertHistoryAsync(
                connection, transaction, resultId, "bo-thay-the",
                oldValue: item.PendingValue, newValue: item.Value, reason: null, actorId, clock.GetUtcNow());

            return true;
        });
    }

    public async Task<bool> TryAutoApproveAsync(Guid sampleId)
    {
        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            var approvedAt = await LockSampleAsync(connection, transaction, sampleId);
            if (approvedAt is not null)
            {
                return false;
            }

            var leaves = (await connection.QueryAsync<LeafRow>(
                """
                SELECT sv.auto_approve, r.value, r.flag, r.instrument_flag, r.pending_value
                FROM order_item oi
                JOIN service sv ON sv.id = oi.service_id AND sv.is_panel = false
                LEFT JOIN result r ON r.order_item_id = oi.id
                WHERE oi.sample_id = @sampleId
                """,
                new { sampleId }, transaction)).ToList();

            var eligible = leaves.Count > 0 && leaves.All(leaf =>
                leaf.AutoApprove
                && !string.IsNullOrEmpty(leaf.Value)
                && (leaf.Flag is null || leaf.Flag == nameof(ResultFlag.None))
                && string.IsNullOrEmpty(leaf.InstrumentFlag)
                && leaf.PendingValue is null);

            if (!eligible)
            {
                return false;
            }

            await ApproveLockedSampleAsync(connection, transaction, sampleId, actorId: null, clock.GetUtcNow());
            return true;
        });
    }

    /// <summary>Giá trị đã qua đường xử lý chung, sẵn sàng ghi vào <c>result</c>.</summary>
    private sealed record ComputedValue(string? Value, string? Unit, decimal? RefLow, decimal? RefHigh, string? RefText, ResultFlag Flag);

    /// <summary>
    /// Đường xử lý chung cho mọi lần ghi giá trị. Giá trị trống → lưu NULL (không phải "0"),
    /// cờ None. Kiểu <c>so</c>: làm tròn theo số lẻ, chọn khoảng tham chiếu, tính cờ. Kiểu
    /// <c>dinh-tinh</c>: quy đổi theo luật (luật riêng máy chỉ khi có máy), cờ là cờ ghi đè của
    /// luật, khoảng tham chiếu (nếu có) chỉ để hiển thị. Kiểu <c>chu</c>: giữ nguyên, cờ None.
    /// </summary>
    private async Task<ComputedValue> ComputeAsync(ServiceRow service, PatientFacts patient, DateOnly at, Guid? instrumentId, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return new ComputedValue(null, service.Unit, null, null, null, ResultFlag.None);
        }

        switch (service.ValueType)
        {
            case "so":
            {
                var parsed = ResultValue.Parse(trimmed);
                if (parsed.IsEmpty)
                {
                    // Ký hiệu lỗi của máy ("***.*", "**") hoặc chuỗi không phải số: không có giá
                    // trị (không phải 0). Chuỗi thô vẫn còn trong lịch sử/raw_ref.
                    return new ComputedValue(null, service.Unit, null, null, null, ResultFlag.None);
                }

                var normalized = NormalizeNumber(trimmed, parsed, service.Decimals);
                var range = ReferenceRangeSelector.Select(await _catalog.RangesAsync(service.Id), patient, at, instrumentId);
                // Cờ tính trên giá trị đã làm tròn (giá trị sẽ hiển thị/in), không trên số thô.
                var flag = FlagCalculator.Compute(ResultValue.Parse(normalized), range);
                return new ComputedValue(normalized, service.Unit, range?.Low, range?.High, range?.TextRange, flag);
            }

            case "dinh-tinh":
            {
                var rules = await _catalog.QualitativeRulesAsync(service.Id);
                var (display, flagOverride) = QualitativeMapper.Apply(trimmed, service.Id, instrumentId, rules);
                var range = ReferenceRangeSelector.Select(await _catalog.RangesAsync(service.Id), patient, at, instrumentId);
                return new ComputedValue(display, service.Unit, range?.Low, range?.High, range?.TextRange, flagOverride ?? ResultFlag.None);
            }

            default:
                return new ComputedValue(trimmed, service.Unit, null, null, null, ResultFlag.None);
        }
    }

    /// <summary>
    /// Chuỗi số sẽ lưu: có số lẻ thì làm tròn (<see cref="ValueRounding"/>, giữ dấu so sánh);
    /// không có số lẻ thì vẫn chuẩn hóa dấu phẩy thành dấu chấm ("4,8" → "4.8") — trừ hiệu giá
    /// ("1:4", "1/4") giữ nguyên như gõ.
    /// </summary>
    private static string NormalizeNumber(string trimmed, ResultValue parsed, int? decimals)
    {
        if (decimals is not null)
        {
            return ValueRounding.Round(trimmed, decimals);
        }

        var body = trimmed[(parsed.Comparator?.Length ?? 0)..].Trim();
        if (body.Contains(':') || body.Contains('/'))
        {
            return trimmed;
        }

        return (parsed.Comparator ?? "") + body.Replace(',', '.');
    }

    private const string JoinResultToSample = "JOIN order_item oi ON oi.id = r.order_item_id WHERE oi.sample_id = @sampleId";

    /// <summary>Khóa dòng mẫu trong giao dịch và trả về <c>approved_at</c> hiện tại.</summary>
    private static async Task<DateTimeOffset?> LockSampleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sampleId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<SampleLockRow>(
            "SELECT id, approved_at FROM sample WHERE id = @sampleId FOR UPDATE", new { sampleId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy mẫu");
        return row.ApprovedAt;
    }

    /// <summary>Duyệt mẫu đã khóa: mọi kết quả có giá trị sang <c>da-duyet</c> + dòng lịch sử <c>duyet</c>; <paramref name="actorId"/> null là tự duyệt.</summary>
    private static async Task ApproveLockedSampleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sampleId, Guid? actorId, DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            $"""
            INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at)
            SELECT r.id, 'duyet', NULL, r.value, NULL, @actorId, @now
            FROM result r {JoinResultToSample} AND r.value IS NOT NULL AND r.value <> ''
            """,
            new { sampleId, actorId, now }, transaction);

        await connection.ExecuteAsync(
            $"""
            UPDATE result r SET status = @status, updated_at = @now, updated_by = @actorId
            FROM order_item oi
            WHERE oi.id = r.order_item_id AND oi.sample_id = @sampleId AND r.value IS NOT NULL AND r.value <> ''
            """,
            new { sampleId, status = StatusApproved, now, actorId }, transaction);

        await connection.ExecuteAsync(
            "UPDATE sample SET approved_at = @now, approved_by = @actorId WHERE id = @sampleId",
            new { sampleId, now, actorId }, transaction);
    }

    /// <summary>Hủy duyệt mẫu đã khóa: mọi kết quả về <c>cho-duyet</c>, dòng lịch sử <c>huy-duyet</c> cho kết quả đã duyệt, xóa <c>approved_at/by</c>.</summary>
    private static async Task UnapproveLockedSampleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sampleId, Guid actorId, string reason, DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            $"""
            INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at)
            SELECT r.id, 'huy-duyet', NULL, r.value, @reason, @actorId, @now
            FROM result r {JoinResultToSample} AND r.status = @approved
            """,
            new { sampleId, reason, actorId, now, approved = StatusApproved }, transaction);

        await connection.ExecuteAsync(
            """
            UPDATE result r SET status = @status, updated_at = @now, updated_by = @actorId
            FROM order_item oi
            WHERE oi.id = r.order_item_id AND oi.sample_id = @sampleId
            """,
            new { sampleId, status = StatusPending, now, actorId }, transaction);

        await connection.ExecuteAsync(
            "UPDATE sample SET approved_at = NULL, approved_by = NULL WHERE id = @sampleId",
            new { sampleId }, transaction);
    }

    private static async Task InsertHistoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid resultId, string action,
        string? oldValue, string? newValue, string? reason, Guid? actorId, DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO result_history (result_id, action, old_value, new_value, reason, actor_id, at)
            VALUES (@resultId, @action, @oldValue, @newValue, @reason, @actorId, @now)
            """,
            new { resultId, action, oldValue, newValue, reason, actorId, now }, transaction);
    }

    private static async Task<ServiceRow> LoadServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid serviceId)
    {
        return await connection.QuerySingleOrDefaultAsync<ServiceRow>(
            "SELECT id, unit, value_type, decimals, is_panel FROM service WHERE id = @serviceId",
            new { serviceId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy dịch vụ");
    }

    /// <summary>
    /// Dòng chỉ định + mẫu + người bệnh + kết quả hiện có — ngữ cảnh cho mọi lần ghi.
    /// <paramref name="resultJoin"/> là "LEFT JOIN" khi chỉ số có thể chưa có kết quả (nhập tay)
    /// hoặc "JOIN" khi đi từ kết quả có sẵn (bản thay thế) — FOR UPDATE không khóa được bảng ở
    /// phía ngoài của LEFT JOIN.
    /// </summary>
    private static string ItemContextSelect(string resultJoin) => $"""
        SELECT oi.id AS order_item_id, oi.sample_id, oi.service_id, s.approved_at,
               p.sex, p.dob, p.birth_year, o.created_at AS order_created_at,
               r.id AS result_id, r.value, r.pending_value, r.pending_instrument_id, r.pending_raw_ref
        FROM order_item oi
        JOIN sample s ON s.id = oi.sample_id
        JOIN lab_order o ON o.id = oi.order_id
        JOIN patient p ON p.id = o.patient_id
        {resultJoin} result r ON r.order_item_id = oi.id
        """;

    private static async Task<ItemContext> LockResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid resultId)
    {
        return await connection.QuerySingleOrDefaultAsync<ItemContext>(
            $"""
            {ItemContextSelect("JOIN")}
            WHERE r.id = @resultId
            FOR UPDATE OF r, s
            """,
            new { resultId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy kết quả");
    }

    private sealed record ItemContext
    {
        public Guid OrderItemId { get; init; }
        public Guid SampleId { get; init; }
        public Guid ServiceId { get; init; }
        public DateTimeOffset? ApprovedAt { get; init; }
        public string Sex { get; init; } = "";
        public DateOnly? Dob { get; init; }
        public int? BirthYear { get; init; }
        public DateTimeOffset OrderCreatedAt { get; init; }
        public Guid? ResultId { get; init; }
        public string? Value { get; init; }
        public string? PendingValue { get; init; }
        public Guid? PendingInstrumentId { get; init; }
        public string? PendingRawRef { get; init; }

        public PatientFacts Patient => new(Sex[0], Dob, BirthYear);

        /// <summary>Ngày chỉ định theo giờ máy — mốc tính tuổi khi chọn khoảng tham chiếu.</summary>
        public DateOnly OrderedLocalDate(TimeProvider clock) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(OrderCreatedAt, clock.LocalTimeZone).DateTime);
    }

    private sealed record ServiceRow
    {
        public Guid Id { get; init; }
        public string? Unit { get; init; }
        public string ValueType { get; init; } = "";
        public int? Decimals { get; init; }
        public bool IsPanel { get; init; }
    }

    private sealed record ExistingResultRow
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
    }

    private sealed record SampleLockRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset? ApprovedAt { get; init; }
    }

    private sealed record LeafRow
    {
        public bool AutoApprove { get; init; }
        public string? Value { get; init; }
        public string? Flag { get; init; }
        public string? InstrumentFlag { get; init; }
        public string? PendingValue { get; init; }
    }
}
