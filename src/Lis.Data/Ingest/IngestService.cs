using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Npgsql;

namespace Lis.Data.Ingest;

/// <summary>Kết quả xử lý một bản tin: số dòng đã ghi, số bản chờ thay thế, số dòng vào Chưa ghép, số dòng QC.</summary>
public sealed record IngestOutcome(int Stored, int PendingReplacement, int Unmatched, int Qc);

/// <summary>Mã lý do trong <c>unmatched_result.reason</c>.</summary>
public static class UnmatchedReason
{
    public const string NoBarcode = "khong-co-barcode";
    public const string NotOrdered = "khong-co-chi-dinh";
    public const string ChannelUnmapped = "kenh-chua-map";
}

/// <summary>
/// Điểm vào cho driver máy (đợt 2): nhận một bản tin đã tách và ghi vào CSDL. Mỗi bản tin
/// là một giao dịch (mọi dòng + dòng Chưa ghép + lần thử tự duyệt cùng commit). Thứ tự xử lý:
/// QC → tìm mẫu theo barcode chính xác → tra map kênh → tra chỉ định → áp map + tính giá trị
/// → ghi (hoặc bản chờ thay thế khi đã duyệt) → thử tự duyệt.
/// </summary>
public sealed class IngestService(Db db, TimeProvider clock)
{
    private const string ReasonStored = "Máy gửi";
    private const string ReasonPending = "Máy gửi lại kết quả cho mẫu đã duyệt";

    private readonly ResultWriter _writer = new(db);
    private readonly ResultService _results = new(db, clock);

    public async Task<IngestOutcome> IngestAsync(InstrumentReading reading)
    {
        // Driver có thể gửi ReceivedAt theo giờ máy (offset +07:00); timestamptz chỉ nhận UTC.
        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            if (reading.IsQc)
            {
                foreach (var item in reading.Items)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO qc_result (instrument_id, barcode, channel_code, value, unit, raw_ref, received_at)
                        VALUES (@instrumentId, @barcode, @channelCode, @value, @unit, @rawRef, @receivedAt)
                        """,
                        new
                        {
                            instrumentId = reading.InstrumentId,
                            barcode = reading.Barcode.Trim(),
                            channelCode = item.ChannelCode.Trim(),
                            value = item.RawValue,
                            unit = item.Unit,
                            rawRef = reading.RawRef,
                            receivedAt = reading.ReceivedAt.ToUniversalTime(),
                        },
                        transaction);
                }

                return new IngestOutcome(0, 0, 0, reading.Items.Count);
            }

            // So khớp chính xác sau khi bỏ khoảng trắng/ký tự điều khiển — không bao giờ LIKE/khớp đuôi.
            var barcode = ReadingNormalizer.CleanBarcode(reading.Barcode);
            var sampleId = barcode.Length == 0
                ? null
                : await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM sample WHERE barcode = @barcode FOR UPDATE", new { barcode }, transaction);

            if (sampleId is null)
            {
                foreach (var item in reading.Items)
                {
                    await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NoBarcode);
                }

                return new IngestOutcome(0, 0, reading.Items.Count, 0);
            }

            var stored = 0;
            var pending = 0;
            var unmatched = 0;

            foreach (var item in reading.Items)
            {
                var channelCode = item.ChannelCode.Trim();
                var channel = await InstrumentStore.FindChannelAsync(connection, transaction, reading.InstrumentId, channelCode);
                if (channel is null)
                {
                    unmatched++;
                    await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.ChannelUnmapped);
                    continue;
                }

                var context = await connection.QueryFirstOrDefaultAsync<ItemContext>(
                    $"""
                    {ResultWriter.ItemContextSelect("LEFT JOIN")}
                    WHERE oi.sample_id = @sampleId AND oi.service_id = @serviceId
                    ORDER BY oi.created_at
                    """,
                    new { sampleId, serviceId = channel.ServiceId }, transaction);

                if (context is null)
                {
                    unmatched++;
                    await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NotOrdered);
                    continue;
                }

                var kind = await InstrumentValuePipeline.WriteAsync(
                    connection, transaction, _writer, clock, context, channel, reading.InstrumentId, item, reading.RawRef,
                    ReasonStored, ReasonPending, actorId: null);

                if (kind == InstrumentWriteKind.Stored)
                {
                    stored++;
                }
                else
                {
                    pending++;
                }
            }

            await _results.TryAutoApproveAsync(connection, transaction, sampleId.Value);

            return new IngestOutcome(stored, pending, unmatched, 0);
        });
    }

    private static async Task InsertUnmatchedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, InstrumentReading reading, string barcode, ReadingItem item, string reason)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, unit, reason, raw_ref, received_at)
            VALUES (@instrumentId, @barcode, @channelCode, @value, @unit, @reason, @rawRef, @receivedAt)
            """,
            new
            {
                instrumentId = reading.InstrumentId,
                barcode,
                channelCode = item.ChannelCode.Trim(),
                value = item.RawValue,
                unit = item.Unit,
                reason,
                rawRef = reading.RawRef,
                receivedAt = reading.ReceivedAt.ToUniversalTime(),
            },
            transaction);
    }
}
