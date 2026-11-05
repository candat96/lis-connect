using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Npgsql;

namespace Lis.Data.Ingest;

/// <summary>Kết cục của một lần ghi giá trị máy: ghi chính thức, hay thành bản chờ thay thế vì mẫu đã duyệt.</summary>
internal enum InstrumentWriteKind
{
    Stored,
    PendingReplacement,
}

/// <summary>
/// Bước 5–6 của nhận kết quả máy, dùng chung cho máy gửi (<see cref="IngestService"/>) và gán
/// tay từ Chưa ghép (<see cref="UnmatchedService"/>): áp map kênh (hệ số, dấu so sánh), tính
/// giá trị qua <see cref="ResultWriter"/> với số lẻ của map, rồi ghi — hoặc chỉ ghi bản chờ thay
/// thế khi kết quả hiện có đã duyệt. Bên gọi phải đã khóa mẫu (thứ tự khóa: mẫu → kết quả).
/// </summary>
internal static class InstrumentValuePipeline
{
    public static async Task<InstrumentWriteKind> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ResultWriter writer, TimeProvider clock,
        ItemContext item, ChannelMap? channel, Guid instrumentId, ReadingItem reading, string? rawRef,
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
            "SELECT id, value, status FROM result WHERE order_item_id = @orderItemId FOR UPDATE",
            new { orderItemId = item.OrderItemId }, transaction);

        // Chưa map (gán tay từ Chưa ghép khi kênh vẫn chưa map): không hệ số, giữ dấu so sánh như gõ tay.
        var adjusted = ReadingNormalizer.AdjustValue(reading.RawValue, channel?.Factor, channel?.KeepComparator ?? true);
        var now = clock.GetUtcNow();

        if (existing is { Status: ResultWriter.StatusApproved })
        {
            await ResultWriter.MarkPendingAsync(
                connection, transaction, existing.Id, existing.Value, adjusted, instrumentId, rawRef, pendingReason, actorId, now);
            return InstrumentWriteKind.PendingReplacement;
        }

        var computed = await writer.ComputeAsync(service, item.Patient, item.OrderedLocalDate(clock), instrumentId, adjusted, channel?.Decimals);
        computed = computed with { Unit = reading.Unit ?? computed.Unit };

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
    }
}
