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

    /// <summary>Ghi một bản tin trong giao dịch riêng (commit nếu xong, rollback nếu lỗi).</summary>
    public Task<IngestOutcome> IngestAsync(InstrumentReading reading) =>
        db.InTransactionAsync((connection, transaction) => IngestAsync(reading, connection, transaction));

    /// <summary>
    /// Cùng logic với <see cref="IngestAsync(InstrumentReading)"/> nhưng chạy trong giao dịch của bên gọi
    /// (không commit/rollback ở đây) — để giải mã một bản tin thô nhiều reading trong MỘT giao dịch.
    /// Khi <see cref="InstrumentReading.RawRef"/> bắt đầu bằng <c>ban-tin:</c>, dòng QC / Chưa ghép đã có từ
    /// trước giao dịch này với cùng (máy, raw_ref, barcode, kênh) bị bỏ qua và không được đếm — giải mã lại không chèn trùng.
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(InstrumentReading reading, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        // Driver có thể gửi ReceivedAt theo giờ máy (offset +07:00); timestamptz chỉ nhận UTC.
        if (reading.IsQc)
        {
            var qcInserted = 0;
            var qcBarcode = ReadingNormalizer.CleanBarcode(reading.Barcode);
            foreach (var item in reading.Items)
            {
                var channelCode = item.ChannelCode.Trim();
                if (IsRawMessageRef(reading.RawRef)
                    && await ExistedBeforeAsync(connection, transaction, "qc_result", reading, qcBarcode, channelCode))
                {
                    continue;
                }

                await connection.ExecuteAsync(
                    """
                    INSERT INTO qc_result (instrument_id, barcode, channel_code, value, unit, raw_ref, received_at)
                    VALUES (@instrumentId, @barcode, @channelCode, @value, @unit, @rawRef, @receivedAt)
                    """,
                    new
                    {
                        instrumentId = reading.InstrumentId,
                        barcode = qcBarcode,
                        channelCode,
                        value = item.RawValue,
                        unit = item.Unit,
                        rawRef = reading.RawRef,
                        receivedAt = reading.ReceivedAt.ToUniversalTime(),
                    },
                    transaction);
                qcInserted++;
            }

            return new IngestOutcome(0, 0, 0, qcInserted);
        }

        // So khớp chính xác sau khi bỏ khoảng trắng/ký tự điều khiển — không bao giờ LIKE/khớp đuôi.
        var barcode = ReadingNormalizer.CleanBarcode(reading.Barcode);
        var sample = barcode.Length == 0
            ? null
            : await connection.QuerySingleOrDefaultAsync<SampleLockRow>(
                "SELECT id, approved_at FROM sample WHERE barcode = @barcode FOR UPDATE", new { barcode }, transaction);

        if (sample is null)
        {
            var noBarcode = 0;
            foreach (var item in reading.Items)
            {
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NoBarcode))
                {
                    noBarcode++;
                }
            }

            return new IngestOutcome(0, 0, noBarcode, 0);
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
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.ChannelUnmapped))
                {
                    unmatched++;
                }

                continue;
            }

            var context = await connection.QueryFirstOrDefaultAsync<ItemContext>(
                $"""
                {ResultWriter.ItemContextSelect("LEFT JOIN")}
                WHERE oi.sample_id = @sampleId AND oi.service_id = @serviceId
                ORDER BY oi.created_at
                """,
                new { sampleId = sample.Id, serviceId = channel.ServiceId }, transaction);

            if (context is null)
            {
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NotOrdered))
                {
                    unmatched++;
                }

                continue;
            }

            var kind = await InstrumentValuePipeline.WriteAsync(
                connection, transaction, _writer, clock, context, sample.ApprovedAt, channel, reading.InstrumentId, item, reading.RawRef,
                ReasonStored, ReasonPending, actorId: null);

            if (kind == InstrumentWriteKind.Stored)
            {
                stored++;
            }
            else if (kind == InstrumentWriteKind.PendingReplacement)
            {
                pending++;
            }
        }

        await _results.TryAutoApproveAsync(connection, transaction, sample.Id);

        return new IngestOutcome(stored, pending, unmatched, 0);
    }

    private static bool IsRawMessageRef(string? rawRef) => rawRef?.StartsWith("ban-tin:", StringComparison.Ordinal) == true;

    /// <summary>
    /// Đã có dòng cùng (máy, raw_ref, barcode, kênh) do giao dịch KHÁC ghi từ trước? Dòng do chính giao dịch
    /// này vừa ghi (xmin = xid hiện tại) không tính: một bản tin có hai kết quả cùng mẫu + kênh thì lần
    /// giải mã đầu vẫn ghi đủ cả hai.
    /// </summary>
    private static async Task<bool> ExistedBeforeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, InstrumentReading reading, string barcode, string channelCode) =>
        await connection.ExecuteScalarAsync<bool>(
            $"""
            SELECT EXISTS (
                SELECT 1 FROM {table}
                WHERE instrument_id = @instrumentId AND raw_ref = @rawRef AND barcode = @barcode AND channel_code = @channelCode
                  AND xmin::text::bigint <> (txid_current() % 4294967296))
            """,
            new { instrumentId = reading.InstrumentId, rawRef = reading.RawRef, barcode, channelCode },
            transaction);

    /// <summary>Chèn một dòng Chưa ghép; trả false khi bỏ qua vì đã có (giải mã lại bản tin thô).</summary>
    private static async Task<bool> InsertUnmatchedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, InstrumentReading reading, string barcode, ReadingItem item, string reason)
    {
        var channelCode = item.ChannelCode.Trim();
        if (IsRawMessageRef(reading.RawRef)
            && await ExistedBeforeAsync(connection, transaction, "unmatched_result", reading, barcode, channelCode))
        {
            return false;
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, unit, reason, instrument_flag, raw_ref, received_at)
            VALUES (@instrumentId, @barcode, @channelCode, @value, @unit, @reason, @instrumentFlag, @rawRef, @receivedAt)
            """,
            new
            {
                instrumentId = reading.InstrumentId,
                barcode,
                channelCode,
                value = item.RawValue,
                unit = item.Unit,
                reason,
                instrumentFlag = string.IsNullOrEmpty(item.InstrumentFlag) ? null : item.InstrumentFlag,
                rawRef = reading.RawRef,
                receivedAt = reading.ReceivedAt.ToUniversalTime(),
            },
            transaction);
        return true;
    }

    private sealed record SampleLockRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset? ApprovedAt { get; init; }
    }
}
