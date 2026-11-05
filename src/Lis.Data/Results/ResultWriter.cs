using Dapper;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Catalog;
using Npgsql;

namespace Lis.Data.Results;

/// <summary>Giá trị đã qua đường xử lý chung, sẵn sàng ghi vào <c>result</c>.</summary>
internal sealed record ComputedValue(string? Value, string? Unit, decimal? RefLow, decimal? RefHigh, string? RefText, ResultFlag Flag);

/// <summary>Nguồn gốc của giá trị sắp ghi: nhập tay, hoặc máy gửi (kèm máy, tham chiếu bản tin thô, cờ máy).</summary>
internal sealed record ResultOrigin(string Source, Guid? InstrumentId, string? RawRef, string? InstrumentFlag)
{
    public static readonly ResultOrigin Manual = new(ResultWriter.SourceManual, null, null, null);

    public static ResultOrigin Instrument(Guid instrumentId, string? rawRef, string? instrumentFlag) =>
        new(ResultWriter.SourceInstrument, instrumentId, rawRef, string.IsNullOrEmpty(instrumentFlag) ? null : instrumentFlag);
}

/// <summary>Các cột của <c>service</c> cần để tính một giá trị.</summary>
internal sealed record ServiceRow
{
    public Guid Id { get; init; }
    public string? Unit { get; init; }
    public string ValueType { get; init; } = "";
    public int? Decimals { get; init; }
    public bool IsPanel { get; init; }
}

/// <summary>
/// Dòng chỉ định + mẫu + người bệnh + kết quả hiện có — ngữ cảnh cho mọi lần ghi
/// (xem <see cref="ResultWriter.ItemContextSelect"/>).
/// </summary>
internal sealed record ItemContext
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
    public string? Status { get; init; }
    public string? PendingValue { get; init; }
    public Guid? PendingInstrumentId { get; init; }
    public string? PendingRawRef { get; init; }
    public string? PendingInstrumentFlag { get; init; }

    public PatientFacts Patient => new(Sex[0], Dob, BirthYear);

    /// <summary>Ngày chỉ định theo giờ máy — mốc tính tuổi khi chọn khoảng tham chiếu.</summary>
    public DateOnly OrderedLocalDate(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(OrderCreatedAt, clock.LocalTimeZone).DateTime);
}

/// <summary>
/// Đường ghi giá trị dùng chung cho nhập tay (<see cref="ResultService"/>), máy gửi
/// (<c>IngestService</c>) và gán tay từ Chưa ghép (<c>UnmatchedService</c>): tính giá trị
/// (<see cref="ComputeAsync"/>) rồi ghi <c>result</c> + <c>result_history</c> trên kết nối /
/// giao dịch của bên gọi. Không có logic nào được chép lại ở các service.
/// </summary>
internal sealed class ResultWriter(Db db)
{
    public const string StatusPending = "cho-duyet";
    public const string StatusApproved = "da-duyet";

    public const string SourceManual = "tay";
    public const string SourceInstrument = "may";

    private readonly CatalogStore _catalog = new(db);

    /// <summary>
    /// Đường xử lý chung cho mọi lần ghi giá trị. Giá trị trống → lưu NULL (không phải "0"),
    /// cờ None. Kiểu <c>so</c>: làm tròn theo số lẻ (<paramref name="decimalsOverride"/> của
    /// map kênh máy nếu có, không thì của chỉ số), chọn khoảng tham chiếu, tính cờ. Kiểu
    /// <c>dinh-tinh</c>: quy đổi theo luật (luật riêng máy chỉ khi có máy), cờ là cờ ghi đè của
    /// luật, khoảng tham chiếu (nếu có) chỉ để hiển thị. Kiểu <c>chu</c>: giữ nguyên, cờ None.
    /// </summary>
    public async Task<ComputedValue> ComputeAsync(
        ServiceRow service, PatientFacts patient, DateOnly at, Guid? instrumentId, string raw, int? decimalsOverride = null)
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

                var normalized = NormalizeNumber(trimmed, parsed, decimalsOverride ?? service.Decimals);
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
    /// Dùng khi giá trị đã tính xong rồi (<c>pending_value</c> đã qua <see cref="ComputeAsync"/>
    /// lúc ghi bản chờ thay thế — xem R22/<c>InstrumentValuePipeline</c>): với kiểu <c>so</c> chỉ
    /// tính lại khoảng tham chiếu/cờ, KHÔNG làm tròn lại — nếu không, "Chấp nhận bản thay thế" có
    /// thể ra khác giá trị lúc mới đến khi số lẻ của map máy khác số lẻ của chỉ số (vd map 1 số
    /// lẻ ra "40.5", làm tròn lại theo chỉ số 2 số lẻ sẽ thành "40.50"). Kiểu khác không có khái
    /// niệm làm tròn nên tính lại bình thường.
    /// </summary>
    public async Task<ComputedValue> ComputeFromFinalValueAsync(
        ServiceRow service, PatientFacts patient, DateOnly at, Guid? instrumentId, string? finalValue)
    {
        if (string.IsNullOrEmpty(finalValue))
        {
            return new ComputedValue(null, service.Unit, null, null, null, ResultFlag.None);
        }

        if (service.ValueType != "so")
        {
            return await ComputeAsync(service, patient, at, instrumentId, finalValue);
        }

        var parsed = ResultValue.Parse(finalValue);
        if (parsed.IsEmpty)
        {
            // Phòng khi pending_value không phải số hợp lệ (dữ liệu cũ trước bản sửa này, hoặc
            // kiểm thử ghi thẳng) — không có giá trị, không phải "0", giống ComputeAsync.
            return new ComputedValue(null, service.Unit, null, null, null, ResultFlag.None);
        }

        var range = ReferenceRangeSelector.Select(await _catalog.RangesAsync(service.Id), patient, at, instrumentId);
        var flag = FlagCalculator.Compute(parsed, range);
        return new ComputedValue(finalValue, service.Unit, range?.Low, range?.High, range?.TextRange, flag);
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

    /// <summary>
    /// Ghi giá trị chính thức mới cho một dòng chỉ định (thêm hoặc đè dòng <c>result</c>), trạng
    /// thái về <c>cho-duyet</c>, kèm dòng lịch sử. Không đụng tới các cột <c>pending_*</c>.
    /// <paramref name="actorId"/> null là máy/hệ thống ghi. Trả về id của <c>result</c>.
    /// </summary>
    public static async Task<Guid> WriteValueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderItemId, ComputedValue computed, ResultOrigin origin,
        string action, string? oldValue, string? reason, Guid? actorId, DateTimeOffset now)
    {
        var resultId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO result
                (order_item_id, value, unit, ref_low, ref_high, ref_text, flag, source, instrument_id, raw_ref,
                 instrument_flag, status, updated_at, updated_by)
            VALUES
                (@orderItemId, @value, @unit, @refLow, @refHigh, @refText, @flag, @source, @instrumentId, @rawRef,
                 @instrumentFlag, @status, @now, @actorId)
            ON CONFLICT (order_item_id) DO UPDATE SET
                value = EXCLUDED.value, unit = EXCLUDED.unit,
                ref_low = EXCLUDED.ref_low, ref_high = EXCLUDED.ref_high, ref_text = EXCLUDED.ref_text,
                flag = EXCLUDED.flag, source = EXCLUDED.source, instrument_id = EXCLUDED.instrument_id, raw_ref = EXCLUDED.raw_ref,
                instrument_flag = EXCLUDED.instrument_flag, status = EXCLUDED.status,
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
                source = origin.Source,
                instrumentId = origin.InstrumentId,
                rawRef = origin.RawRef,
                instrumentFlag = origin.InstrumentFlag,
                status = StatusPending,
                now,
                actorId,
            },
            transaction);

        await InsertHistoryAsync(connection, transaction, resultId, action, oldValue, computed.Value, reason, actorId, now);
        return resultId;
    }

    /// <summary>
    /// Máy gửi (lại) kết quả cho mẫu đã duyệt: không ghi đè giá trị chính thức, chỉ ghi bản chờ
    /// thay thế (<c>pending_*</c>) và dòng lịch sử <c>thay-the</c>. Mẫu giữ nguyên trạng thái
    /// đã duyệt. <paramref name="actorId"/> null khi máy gửi.
    /// </summary>
    public static async Task MarkPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid resultId, string? currentValue,
        string pendingValue, Guid instrumentId, string? rawRef, string? instrumentFlag, string reason, Guid? actorId, DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            UPDATE result SET pending_value = @pendingValue, pending_instrument_id = @instrumentId, pending_raw_ref = @rawRef,
                pending_instrument_flag = @instrumentFlag
            WHERE id = @resultId
            """,
            new { resultId, pendingValue, instrumentId, rawRef, instrumentFlag = string.IsNullOrEmpty(instrumentFlag) ? null : instrumentFlag },
            transaction);

        await InsertHistoryAsync(connection, transaction, resultId, "thay-the", currentValue, pendingValue, reason, actorId, now);
    }

    public static async Task InsertHistoryAsync(
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

    public static async Task<ServiceRow> LoadServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid serviceId)
    {
        return await connection.QuerySingleOrDefaultAsync<ServiceRow>(
            "SELECT id, unit, value_type, decimals, is_panel FROM service WHERE id = @serviceId",
            new { serviceId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy dịch vụ");
    }

    /// <summary>Khóa dòng mẫu trong giao dịch và trả về <c>approved_at</c> hiện tại. Quy tắc thứ tự khóa: mẫu trước, kết quả sau.</summary>
    public static async Task<DateTimeOffset?> LockSampleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sampleId)
    {
        var row = await connection.QuerySingleOrDefaultAsync<SampleLockRow>(
            "SELECT id, approved_at FROM sample WHERE id = @sampleId FOR UPDATE", new { sampleId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy mẫu");
        return row.ApprovedAt;
    }

    /// <summary>
    /// Câu SELECT lấy <see cref="ItemContext"/>. <paramref name="resultJoin"/> là "LEFT JOIN" khi
    /// chỉ số có thể chưa có kết quả hoặc "JOIN" khi đi từ kết quả có sẵn (bản thay thế) —
    /// FOR UPDATE không khóa được bảng ở phía ngoài của LEFT JOIN.
    /// </summary>
    public static string ItemContextSelect(string resultJoin) => $"""
        SELECT oi.id AS order_item_id, oi.sample_id, oi.service_id, s.approved_at,
               p.sex, p.dob, p.birth_year, o.created_at AS order_created_at,
               r.id AS result_id, r.value, r.status, r.pending_value, r.pending_instrument_id, r.pending_raw_ref,
               r.pending_instrument_flag
        FROM order_item oi
        JOIN sample s ON s.id = oi.sample_id
        JOIN lab_order o ON o.id = oi.order_id
        JOIN patient p ON p.id = o.patient_id
        {resultJoin} result r ON r.order_item_id = oi.id
        """;

    private sealed record SampleLockRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset? ApprovedAt { get; init; }
    }
}
