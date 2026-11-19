using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Npgsql;

namespace Lis.Data.Ingest;

/// <summary>
/// Chế độ ghi giá trị máy. <see cref="Live"/>: máy gửi (hoặc gán tay từ Chưa ghép) — ghi như thường lệ.
/// <see cref="Redecode"/>: giải mã lại một bản tin thô đã lưu — không bao giờ ghi đè hay xoá âm thầm một kết quả
/// đã có (xem <see cref="InstrumentValuePipeline"/>).
/// </summary>
public enum ValueWriteMode
{
    Live,
    Redecode,
}

/// <summary>
/// Kết cục của một lần ghi giá trị máy: ghi chính thức, thành bản chờ thay thế, không đổi gì
/// (máy gửi lại đúng giá trị đã duyệt, không có bản chờ thay thế nào đang treo), hoặc bỏ một
/// bản chờ thay thế cũ không còn đúng nữa (máy gửi lại đúng giá trị đã duyệt trong khi đang có
/// bản chờ thay thế từ lần gửi trước), hoặc — chỉ ở chế độ <see cref="ValueWriteMode.Redecode"/> —
/// <c>Conflict</c>: giải mã lại ra giá trị khác nhưng đang có bản chờ thay thế của bản tin khác, không ghi.
/// </summary>
internal enum InstrumentWriteKind
{
    Stored,
    PendingReplacement,
    Unchanged,
    PendingCleared,
    Conflict,

    /// <summary>Chỉ ở chế độ Redecode: kết quả hiện có do bản tin MỚI hơn bản tin đang giải mã ghi — không tạo chờ thay, không ghi gì.</summary>
    OlderThanCurrent,
}

/// <summary>
/// Bước 5–6 của nhận kết quả máy, dùng chung cho máy gửi (<see cref="IngestService"/>) và gán
/// tay từ Chưa ghép (<see cref="UnmatchedService"/>): áp map kênh (hệ số, dấu so sánh, số lẻ —
/// chỉ khi map thật sự trỏ đúng chỉ số của dòng này), tính giá trị qua <see cref="ResultWriter"/>,
/// rồi ghi — hoặc ghi bản chờ thay thế khi <b>mẫu</b> đã duyệt (không phải riêng dòng này đã
/// duyệt: một chỉ số trước đó chưa từng có giá trị vẫn phải qua bản chờ thay thế nếu mẫu đã
/// duyệt, không được âm thầm có giá trị mới mà không ai duyệt). Bên gọi phải đã khóa mẫu trước
/// (thứ tự khóa dùng chung toàn hệ thống: mẫu → chỉ định → kết quả) và truyền
/// <paramref name="sampleApprovedAt"/> đọc được từ chính lần khóa đó — không dùng lại
/// <see cref="ItemContext.ApprovedAt"/> vì giá trị đó có thể đọc trước khi khóa.
/// <para>
/// Chế độ <see cref="ValueWriteMode.Redecode"/> với chỉ số ĐÃ có giá trị (bất kể mẫu đã duyệt hay chưa, nguồn máy hay tay):
/// giống bản giải mã lại (giá trị và cờ máy) thì <see cref="InstrumentWriteKind.Unchanged"/>, không đụng bản chờ thay đang có;
/// khác thì vào bản chờ thay, không bao giờ đè giá trị hiện tại; nếu đang có bản chờ thay của bản tin khác thì
/// <see cref="InstrumentWriteKind.Conflict"/> (không ghi đè nó). Không bao giờ trả <see cref="InstrumentWriteKind.PendingCleared"/>.
/// Chỉ số chưa có giá trị thì ghi như thường lệ (bộ giải mã đã sửa nay đọc thêm được kênh).
/// </para>
/// </summary>
internal static class InstrumentValuePipeline
{
    public static async Task<InstrumentWriteKind> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ResultWriter writer, TimeProvider clock,
        ItemContext item, DateTimeOffset? sampleApprovedAt, ChannelMap? channel, Guid instrumentId, ReadingItem reading, string? rawRef,
        string storeReason, string pendingReason, Guid? actorId, ValueWriteMode mode = ValueWriteMode.Live, string? conflictReason = null)
    {
        var service = await ResultWriter.LoadServiceAsync(connection, transaction, item.ServiceId);
        if (service.IsPanel)
        {
            throw new InvalidOperationException("Dịch vụ cha (panel) không có giá trị kết quả");
        }

        // Đọc lại và khóa kết quả hiện có *sau khi* đã giữ khóa mẫu — bản LEFT JOIN trong
        // ItemContext có thể cũ khi một máy trạm khác vừa ghi/duyệt xong.
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingResultRow>(
            "SELECT id, value, status, source, raw_ref, pending_value, pending_raw_ref, pending_instrument_flag, instrument_flag FROM result WHERE order_item_id = @orderItemId FOR UPDATE",
            new { orderItemId = item.OrderItemId }, transaction);

        // Map kênh (hệ số/số lẻ/giữ dấu so sánh) chỉ áp khi nó thật sự trỏ tới đúng chỉ số của
        // dòng này — map tra theo (instrument, channel_code) có thể không khớp serviceId khi gán
        // tay từ Chưa ghép vào một chỉ số khác với chỉ số map hiện đang trỏ tới.
        var channelMatches = channel is not null && channel.ServiceId == item.ServiceId;
        var adjusted = ReadingNormalizer.AdjustValue(
            reading.RawValue, channelMatches ? channel!.Factor : null, channelMatches ? channel!.KeepComparator : true);
        var decimalsOverride = channelMatches ? channel!.Decimals : null;

        var computed = await writer.ComputeAsync(service, item.Patient, item.OrderedLocalDate(clock), instrumentId, adjusted, decimalsOverride);
        var now = clock.GetUtcNow();

        if (mode == ValueWriteMode.Redecode && existing is { Value: { Length: > 0 } currentValue })
        {
            if (currentValue == computed.Value && NormalFlag(existing.InstrumentFlag) == NormalFlag(reading.InstrumentFlag))
            {
                return InstrumentWriteKind.Unchanged;
            }

            // Bản tin cũ hơn không tạo chờ thay đè lên kết quả máy do bản tin mới hơn ghi (giá trị sửa tay, đường cũ, bản tin
            // cũ hơn hoặc cùng thứ tự thì vẫn vào chờ thay).
            if (existing.Source == "may" && await IsWrittenByNewerMessageAsync(connection, transaction, existing.RawRef, rawRef))
            {
                return InstrumentWriteKind.OlderThanCurrent;
            }

            if (existing.PendingValue is not null)
            {
                if (existing.PendingRawRef != rawRef)
                {
                    await ResultWriter.InsertHistoryAsync(
                        connection, transaction, existing.Id, "giai-ma-lai-khac", currentValue, computed.Value ?? adjusted,
                        conflictReason, actorId: null, now);
                    return InstrumentWriteKind.Conflict;
                }

                if (existing.PendingValue == (computed.Value ?? "") && NormalFlag(existing.PendingInstrumentFlag) == NormalFlag(reading.InstrumentFlag))
                {
                    // Chính bản tin này đã đặt bản chờ thay đúng giá trị đó (giải mã lại lần nữa) — không ghi trùng.
                    return InstrumentWriteKind.Unchanged;
                }
            }

            await ResultWriter.MarkPendingAsync(
                connection, transaction, existing.Id, currentValue, computed.Value ?? "", adjusted, computed.Flag, instrumentId, rawRef,
                reading.InstrumentFlag, pendingReason, actorId, now);
            return InstrumentWriteKind.PendingReplacement;
        }

        if (sampleApprovedAt is not null)
        {
            if (mode == ValueWriteMode.Redecode && existing is { PendingValue: not null } && existing.PendingRawRef != rawRef)
            {
                // Chỉ số chưa có giá trị chính thức nhưng đang treo bản chờ thay của bản tin khác: không ghi đè nó.
                await ResultWriter.InsertHistoryAsync(
                    connection, transaction, existing.Id, "giai-ma-lai-khac", existing.Value, computed.Value ?? adjusted, conflictReason, actorId: null, now);
                return InstrumentWriteKind.Conflict;
            }

            if (existing is { Status: ResultWriter.StatusApproved } && existing.Value == computed.Value)
            {
                if (existing.PendingValue is not null && mode == ValueWriteMode.Live)
                {
                    // Máy gửi lại đúng giá trị đã duyệt trong khi đang có bản chờ thay thế từ lần
                    // gửi trước (vd lần đó đọc lỗi) — bản chờ thay thế đó không còn đúng nữa, bỏ đi
                    // thay vì để treo một bản thay thế sai gợi ý người dùng xem lại nhầm.
                    await connection.ExecuteAsync(
                        """
                        UPDATE result SET pending_value = NULL, pending_instrument_id = NULL, pending_raw_ref = NULL,
                            pending_instrument_flag = NULL, pending_flag = NULL
                        WHERE id = @resultId
                        """,
                        new { resultId = existing.Id }, transaction);

                    await ResultWriter.InsertHistoryAsync(
                        connection, transaction, existing.Id, "bo-thay-the", existing.PendingValue, existing.Value,
                        "Máy gửi lại giá trị đã duyệt", actorId: null, now);
                    return InstrumentWriteKind.PendingCleared;
                }

                // Không có bản chờ thay thế nào đang treo: không tạo thêm pending/lịch sử trùng lặp.
                return InstrumentWriteKind.Unchanged;
            }

            // Mẫu đã duyệt: giá trị máy gửi luôn thành bản chờ thay thế, kể cả khi chỉ số này
            // trước đó chưa từng có kết quả nào (tạo dòng result rỗng để treo pending_* lên) —
            // không bao giờ ghi thẳng vào một mẫu đã duyệt.
            var resultId = existing?.Id ?? await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO result (order_item_id, unit, status, updated_at, updated_by)
                VALUES (@orderItemId, @unit, @status, @now, @actorId)
                RETURNING id
                """,
                new { orderItemId = item.OrderItemId, unit = service.Unit, status = ResultWriter.StatusPending, now, actorId },
                transaction);

            // historyNewValue dùng chuỗi thô đã áp map (adjusted) — giữ lại dấu vết ngay cả khi
            // không đọc được thành giá trị ("***.*" → computed.Value null, pending_value lưu "").
            await ResultWriter.MarkPendingAsync(
                connection, transaction, resultId, existing?.Value, computed.Value ?? "", adjusted, computed.Flag, instrumentId, rawRef,
                reading.InstrumentFlag, pendingReason, actorId, now);
            return InstrumentWriteKind.PendingReplacement;
        }

        await ResultWriter.WriteValueAsync(
            connection, transaction, item.OrderItemId, computed, ResultOrigin.Instrument(instrumentId, rawRef, reading.InstrumentFlag),
            "nhan", existing?.Value, storeReason, actorId, now);
        return InstrumentWriteKind.Stored;
    }

    /// <summary>
    /// Chuẩn hoá cờ máy CHỈ để so sánh (không đổi dữ liệu lưu): bỏ khoảng trắng, tách theo <c>~</c> và <c>;</c>, bỏ phần rỗng và
    /// phần <c>N</c> (bình thường, không phân biệt hoa thường), sắp xếp rồi nối bằng <c>~</c>; rỗng thì <c>null</c>.
    /// Nhờ đó cờ trống (nhập tay) và <c>N</c> (HL7 báo bình thường) coi là một.
    /// </summary>
    internal static string? NormalFlag(string? flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return null;
        }

        var parts = flag.Trim()
            .Split(['~', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.Equals(p, "N", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return parts.Length == 0 ? null : string.Join('~', parts);
    }

    /// <summary>
    /// <paramref name="existingRawRef"/> dạng <c>ban-tin:X</c>, bản tin X tồn tại và (<c>completed_at</c>, <c>seq</c>) của X MỚI hơn
    /// bản tin đang giải mã (<paramref name="currentRawRef"/>). Không phân tích được hoặc không tìm thấy thì false (vào chờ thay như thường).
    /// </summary>
    private static async Task<bool> IsWrittenByNewerMessageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string? existingRawRef, string? currentRawRef)
    {
        if (!TryParseMessageRef(existingRawRef, out var existingId) || !TryParseMessageRef(currentRawRef, out var currentId) || existingId == currentId)
        {
            return false;
        }

        return await connection.ExecuteScalarAsync<bool>(
            """
            SELECT COALESCE(
                (SELECT (e.completed_at, e.seq) > (c.completed_at, c.seq)
                 FROM raw_message e, raw_message c
                 WHERE e.id = @existingId AND c.id = @currentId), false)
            """,
            new { existingId, currentId }, transaction);
    }

    private static bool TryParseMessageRef(string? rawRef, out Guid id)
    {
        const string prefix = "ban-tin:";
        id = default;
        return rawRef is not null && rawRef.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(rawRef.AsSpan(prefix.Length), out id);
    }

    private sealed record ExistingResultRow
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
        public string Status { get; init; } = "";
        public string? Source { get; init; }
        public string? RawRef { get; init; }
        public string? PendingValue { get; init; }
        public string? PendingRawRef { get; init; }
        public string? PendingInstrumentFlag { get; init; }
        public string? InstrumentFlag { get; init; }
    }
}
