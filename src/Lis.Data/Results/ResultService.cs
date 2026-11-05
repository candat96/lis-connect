using Dapper;
using Lis.Core.Patients;
using Lis.Core.Results;
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
/// Nghiệp vụ kết quả phía người dùng: nhập tay, duyệt / hủy duyệt, tự duyệt, chấp nhận / bỏ
/// bản thay thế. Mọi lần ghi giá trị đi qua <see cref="ResultWriter"/> (đường tính giá trị và
/// ghi <c>result</c> + <c>result_history</c> dùng chung với máy gửi). Mọi thao tác ghi nằm
/// trong một giao dịch.
/// </summary>
public sealed class ResultService(Db db, TimeProvider clock) : IResultService
{
    private const string StatusPending = ResultWriter.StatusPending;
    private const string StatusApproved = ResultWriter.StatusApproved;

    private readonly ResultWriter _writer = new(db);
    private readonly SampleResultsQuery _query = new(db, clock);

    public Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query) =>
        _query.ListAsync(from, to, status, query);

    public Task<SampleResults> GetAsync(Guid sampleId) => _query.GetAsync(sampleId);

    public async Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            // Khóa mẫu TRƯỚC, bằng một câu lệnh riêng — không gộp vào câu SELECT đọc chỉ định:
            // Postgres có thể khóa order_item trước sample khi FOR UPDATE OF liệt kê cả hai trong
            // cùng một JOIN (JOIN bắt đầu từ order_item), đảo ngược thứ tự khóa dùng chung toàn hệ
            // thống (mẫu → chỉ định → kết quả) và có thể gây deadlock với đường máy gửi.
            var sampleId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT sample_id FROM order_item WHERE id = @orderItemId", new { orderItemId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng chỉ định");

            var approvedAt = await ResultWriter.LockSampleAsync(connection, transaction, sampleId);
            if (approvedAt is not null)
            {
                throw new InvalidOperationException("Mẫu đã duyệt — hủy duyệt trước khi sửa");
            }

            var item = await connection.QuerySingleOrDefaultAsync<ItemContext>(
                $"""
                {ResultWriter.ItemContextSelect("LEFT JOIN")}
                WHERE oi.id = @orderItemId
                FOR UPDATE OF oi, s
                """,
                new { orderItemId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng chỉ định");

            var service = await ResultWriter.LoadServiceAsync(connection, transaction, item.ServiceId);
            if (service.IsPanel)
            {
                throw new InvalidOperationException("Dịch vụ cha (panel) không có giá trị kết quả");
            }

            // Nhập tay chỉ số số mà gõ chuỗi không đọc được thành số ("abc", "12..5", kể cả ký
            // hiệu lỗi của máy "***.*") thì từ chối ngay — nếu lặng lẽ lưu NULL, người nhập
            // tưởng đã lưu và mất luôn chuỗi đã gõ. Đường máy gửi (AcceptPending) vẫn lưu NULL
            // và giữ chuỗi thô trong raw_ref/lịch sử.
            var trimmedValue = value.Trim();
            if (service.ValueType == "so" && trimmedValue.Length > 0 && ResultValue.Parse(trimmedValue).IsEmpty)
            {
                throw new ArgumentException($"Giá trị không hợp lệ: '{trimmedValue}'");
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

            var computed = await _writer.ComputeAsync(service, item.Patient, item.OrderedLocalDate(clock), instrumentId: null, value);

            await ResultWriter.WriteValueAsync(
                connection, transaction, orderItemId, computed, ResultOrigin.Manual,
                hasOldValue ? "sua" : "nhan",
                oldValue,
                reason: hasOldValue ? reason!.Trim() : null,
                actorId, clock.GetUtcNow());

            return true;
        });
    }

    public async Task ApproveAsync(Guid sampleId, Guid actorId)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var approvedAt = await ResultWriter.LockSampleAsync(connection, transaction, sampleId);
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
            var approvedAt = await ResultWriter.LockSampleAsync(connection, transaction, sampleId);
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

            var service = await ResultWriter.LoadServiceAsync(connection, transaction, item.ServiceId);
            // pending_value đã là giá trị tính xong lúc ghi (R22, qua ResultWriter/InstrumentValuePipeline)
            // — không tính lại số lẻ/định tính ở đây, chỉ tính lại cờ/khoảng tham chiếu, để "Chấp
            // nhận" luôn ra đúng giá trị như lúc mới đến (số lẻ của map máy có thể khác số lẻ của
            // chỉ số, tính lại qua ComputeAsync bình thường sẽ làm tròn sai lần hai).
            var computed = await _writer.ComputeFromFinalValueAsync(
                service, item.Patient, item.OrderedLocalDate(clock), item.PendingInstrumentId, item.PendingValue);
            var now = clock.GetUtcNow();

            // Cờ máy gửi kèm bản thay thế (pending_instrument_flag) chuyển thành cờ chính thức —
            // một giá trị có cờ máy không được "sạch cờ" chỉ vì đi qua bản chờ thay thế rồi được
            // chấp nhận (nếu không, tự duyệt sau đó có thể duyệt nhầm một giá trị máy đã gắn cờ).
            await connection.ExecuteAsync(
                """
                UPDATE result SET
                    value = @value, unit = @unit, ref_low = @refLow, ref_high = @refHigh, ref_text = @refText,
                    flag = @flag, source = @source, instrument_id = pending_instrument_id, raw_ref = pending_raw_ref,
                    instrument_flag = @instrumentFlag, status = @status,
                    pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL, pending_instrument_flag = NULL,
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
                    source = ResultWriter.SourceInstrument,
                    instrumentFlag = item.PendingInstrumentFlag,
                    status = StatusPending,
                    now,
                    actorId,
                },
                transaction);

            await ResultWriter.InsertHistoryAsync(connection, transaction, resultId, "thay-the", item.Value, item.PendingValue, reason: null, actorId, now);

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
                "UPDATE result SET pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL, pending_instrument_flag = NULL WHERE id = @resultId",
                new { resultId }, transaction);

            await ResultWriter.InsertHistoryAsync(
                connection, transaction, resultId, "bo-thay-the",
                oldValue: item.PendingValue, newValue: item.Value, reason: null, actorId, clock.GetUtcNow());

            return true;
        });
    }

    public async Task<bool> TryAutoApproveAsync(Guid sampleId)
    {
        return await db.InTransactionAsync((connection, transaction) => TryAutoApproveAsync(connection, transaction, sampleId));
    }

    /// <summary>
    /// Bản chạy trên giao dịch của bên gọi — máy gửi (<c>IngestService</c>) và gán tay từ Chưa
    /// ghép gọi ngay sau khi ghi giá trị để tự duyệt cùng một giao dịch.
    /// </summary>
    internal async Task<bool> TryAutoApproveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sampleId)
    {
        var approvedAt = await ResultWriter.LockSampleAsync(connection, transaction, sampleId);
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
    }

    private const string JoinResultToSample = "JOIN order_item oi ON oi.id = r.order_item_id WHERE oi.sample_id = @sampleId";

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

    /// <summary>
    /// Khóa kết quả kèm mẫu của nó. Quy tắc thứ tự khóa chung cho mọi đường ghi: mẫu trước,
    /// kết quả sau (SetManual: khóa mẫu rồi mới FOR UPDATE result; Approve/Unapprove/AutoApprove:
    /// khóa mẫu rồi cập nhật result) — liệt kê <c>FOR UPDATE OF s, r</c> theo đúng thứ tự đó
    /// để ai đọc cũng thấy cùng một quy tắc, tránh hai giao dịch chờ nhau chéo.
    /// </summary>
    private static async Task<ItemContext> LockResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid resultId)
    {
        // Khóa mẫu TRƯỚC, bằng một câu lệnh riêng — JOIN bắt đầu từ order_item nên một câu
        // FOR UPDATE OF duy nhất không đảm bảo mẫu bị khóa trước chỉ định/kết quả (xem SetManualAsync).
        var sampleId = await connection.ExecuteScalarAsync<Guid?>(
            "SELECT oi.sample_id FROM result r JOIN order_item oi ON oi.id = r.order_item_id WHERE r.id = @resultId",
            new { resultId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy kết quả");

        await ResultWriter.LockSampleAsync(connection, transaction, sampleId);

        return await connection.QuerySingleOrDefaultAsync<ItemContext>(
            $"""
            {ResultWriter.ItemContextSelect("JOIN")}
            WHERE r.id = @resultId
            FOR UPDATE OF oi, s, r
            """,
            new { resultId }, transaction)
            ?? throw new InvalidOperationException("Không tìm thấy kết quả");
    }

    private sealed record ExistingResultRow
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
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
