using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Npgsql;

namespace Lis.Data.Ingest;

/// <summary>
/// Kết cục của một lần ghi giá trị máy: ghi chính thức, thành bản chờ thay thế, không đổi gì
/// (máy gửi lại đúng giá trị đã duyệt, không có bản chờ thay thế nào đang treo), hoặc bỏ một
/// bản chờ thay thế cũ không còn đúng nữa (máy gửi lại đúng giá trị đã duyệt trong khi đang có
/// bản chờ thay thế từ lần gửi trước).
/// </summary>
internal enum InstrumentWriteKind
{
    Stored,
    PendingReplacement,
    Unchanged,
    PendingCleared,
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
/// </summary>
internal static class InstrumentValuePipeline
{
    public static async Task<InstrumentWriteKind> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ResultWriter writer, TimeProvider clock,
        ItemContext item, DateTimeOffset? sampleApprovedAt, ChannelMap? channel, Guid instrumentId, ReadingItem reading, string? rawRef,
        string storeReason, string pendingReason, Guid? actorId)
    {
        var service = await ResultWriter.LoadServiceAsync(connection, transaction, item.ServiceId);
        if (service.IsPanel)
        {
            throw new InvalidOperationException("Dịch vụ cha (panel) không có giá trị kết quả");
        }

        // Đọc lại và khóa kết quả hiện có *sau khi* đã giữ khóa mẫu — bản LEFT JOIN trong
        // ItemContext có thể cũ khi một máy trạm khác vừa ghi/duyệt xong.
        var existing = await connection.QuerySingleOrDefaultAsync<ExistingResultRow>(
            "SELECT id, value, status, pending_value FROM result WHERE order_item_id = @orderItemId FOR UPDATE",
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

        if (sampleApprovedAt is not null)
        {
            if (existing is { Status: ResultWriter.StatusApproved } && existing.Value == computed.Value)
            {
                if (existing.PendingValue is not null)
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

    private sealed record ExistingResultRow
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
        public string Status { get; init; } = "";
        public string? PendingValue { get; init; }
    }
}
