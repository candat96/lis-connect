using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;
using Npgsql;

namespace Lis.Data.Ingest;

/// <summary>Kết quả xử lý một bản tin: số dòng đã ghi, số bản chờ thay thế, số dòng vào Chưa ghép, số dòng QC.</summary>
public sealed record IngestOutcome(int Stored, int PendingReplacement, int Unmatched, int Qc);

/// <summary>
/// <see cref="IngestOutcome"/> kèm các số liệu mà lượt giải mã bản tin thô cần ghi vào <c>decode_result</c>:
/// <see cref="Unchanged"/> (không ghi gì), <see cref="Conflicts"/> (giải mã lại trùng bản chờ thay của bản tin khác),
/// <see cref="PendingCleared"/> (máy gửi lại đúng giá trị đã duyệt nên bỏ bản chờ thay cũ).
/// </summary>
internal sealed record IngestDetail(
    int Stored, int PendingReplacement, int Unmatched, int Qc, int Unchanged = 0, int Conflicts = 0, int PendingCleared = 0,
    int OlderThanCurrent = 0, IReadOnlyList<ResolvedMismatch>? ResolvedMismatches = null)
{
    public IngestOutcome ToOutcome() => new(Stored, PendingReplacement, Unmatched, Qc);
}

/// <summary>Giải mã lại ra giá trị khác với dòng Chưa ghép ĐÃ xử lý (đã gán / bỏ qua): không tạo dòng mới, báo để người dùng xem lại.</summary>
internal sealed record ResolvedMismatch(Guid UnmatchedId, string Barcode, string Channel, string? OldValue, string NewValue);

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
    private const string ReasonRedecodePending = "Giải mã lại ra giá trị khác";
    private const string ReasonRedecodeConflict = "Giải mã lại ra giá trị khác, đang có giá trị chờ thay từ bản tin khác — không ghi";

    private readonly ResultWriter _writer = new(db);
    private readonly ResultService _results = new(db, clock);

    /// <summary>Ghi một bản tin trong giao dịch riêng (commit nếu xong, rollback nếu lỗi).</summary>
    public Task<IngestOutcome> IngestAsync(InstrumentReading reading) =>
        db.InTransactionAsync((connection, transaction) => IngestAsync(reading, connection, transaction));

    /// <summary>
    /// Cùng logic với <see cref="IngestAsync(InstrumentReading)"/> nhưng chạy trong giao dịch của bên gọi
    /// (không commit/rollback ở đây) — để giải mã một bản tin thô nhiều reading trong MỘT giao dịch.
    /// </summary>
    public Task<IngestOutcome> IngestAsync(InstrumentReading reading, NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        IngestAsync(reading, connection, transaction, ValueWriteMode.Live);

    /// <summary>
    /// Như trên, với <paramref name="mode"/>: <see cref="ValueWriteMode.Redecode"/> khi giải mã lại bản tin thô — không ghi đè
    /// kết quả đã có (xem <see cref="InstrumentValuePipeline"/>), không tự duyệt mẫu, và không tạo lại dòng Chưa ghép đã xử lý.
    /// Bên gọi chịu trách nhiệm xoá dòng QC / Chưa ghép chưa xử lý của lượt trước (một bản tin chỉ chạy <c>tu-dong</c> một lần
    /// nhờ hàng rào lượt chạy nên không cần chống trùng ở đây).
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(InstrumentReading reading, NpgsqlConnection connection, NpgsqlTransaction transaction, ValueWriteMode mode) =>
        (await IngestDetailedAsync(reading, connection, transaction, mode)).ToOutcome();

    internal async Task<IngestDetail> IngestDetailedAsync(
        InstrumentReading reading, NpgsqlConnection connection, NpgsqlTransaction transaction, ValueWriteMode mode)
    {
        // Driver có thể gửi ReceivedAt theo giờ máy (offset +07:00); timestamptz chỉ nhận UTC.
        if (reading.IsQc)
        {
            var qcInserted = 0;
            var qcBarcode = ReadingNormalizer.CleanBarcode(reading.Barcode);
            foreach (var item in reading.Items)
            {
                var channelCode = item.ChannelCode.Trim();
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

            return new IngestDetail(0, 0, 0, qcInserted);
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
            var noBarcodeMismatches = new List<ResolvedMismatch>();
            foreach (var item in reading.Items)
            {
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NoBarcode, mode, noBarcodeMismatches))
                {
                    noBarcode++;
                }
            }

            return new IngestDetail(0, 0, noBarcode, 0, ResolvedMismatches: noBarcodeMismatches);
        }

        var stored = 0;
        var pending = 0;
        var unmatched = 0;
        var unchanged = 0;
        var conflicts = 0;
        var cleared = 0;
        var older = 0;
        var mismatches = new List<ResolvedMismatch>();

        foreach (var item in reading.Items)
        {
            var channelCode = item.ChannelCode.Trim();
            var channel = await InstrumentStore.FindChannelAsync(connection, transaction, reading.InstrumentId, channelCode);
            if (channel is null)
            {
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.ChannelUnmapped, mode, mismatches))
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
                if (await InsertUnmatchedAsync(connection, transaction, reading, barcode, item, UnmatchedReason.NotOrdered, mode, mismatches))
                {
                    unmatched++;
                }

                continue;
            }

            var kind = await InstrumentValuePipeline.WriteAsync(
                connection, transaction, _writer, clock, context, sample.ApprovedAt, channel, reading.InstrumentId, item, reading.RawRef,
                ReasonStored, mode == ValueWriteMode.Redecode ? ReasonRedecodePending : ReasonPending, actorId: null, mode, ReasonRedecodeConflict);

            switch (kind)
            {
                case InstrumentWriteKind.Stored:
                    stored++;
                    break;
                case InstrumentWriteKind.PendingReplacement:
                    pending++;
                    break;
                case InstrumentWriteKind.Unchanged:
                    unchanged++;
                    break;
                case InstrumentWriteKind.PendingCleared:
                    cleared++;
                    break;
                case InstrumentWriteKind.Conflict:
                    conflicts++;
                    break;
                case InstrumentWriteKind.OlderThanCurrent:
                    older++;
                    break;
            }
        }

        // Giải mã lại không tự duyệt mẫu: người dùng chủ động chạy lại nên cũng chủ động duyệt.
        if (mode == ValueWriteMode.Live)
        {
            await _results.TryAutoApproveAsync(connection, transaction, sample.Id);
        }

        return new IngestDetail(stored, pending, unmatched, 0, unchanged, conflicts, cleared, older, mismatches);
    }

    /// <summary>
    /// Chèn một dòng Chưa ghép; trả false khi bỏ qua. Chỉ ở chế độ giải mã lại: đã có dòng ĐÃ XỬ LÝ cùng (máy, raw_ref, barcode, kênh)
    /// thì không tạo dòng mới — dòng đã xử lý được giữ nguyên; nếu giá trị khác thì ghi vào <paramref name="mismatches"/> để báo.
    /// </summary>
    private static async Task<bool> InsertUnmatchedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, InstrumentReading reading, string barcode, ReadingItem item, string reason,
        ValueWriteMode mode, List<ResolvedMismatch> mismatches)
    {
        var channelCode = item.ChannelCode.Trim();
        if (mode == ValueWriteMode.Redecode)
        {
            var resolved = (await connection.QueryAsync<ResolvedRow>(
                """
                SELECT id, value FROM unmatched_result
                WHERE instrument_id = @instrumentId AND raw_ref = @rawRef AND barcode = @barcode AND channel_code = @channelCode
                  AND resolved_at IS NOT NULL
                ORDER BY received_at, id
                """,
                new { instrumentId = reading.InstrumentId, rawRef = reading.RawRef, barcode, channelCode }, transaction)).ToList();
            if (resolved.Count > 0)
            {
                if (!resolved.Any(r => r.Value == item.RawValue))
                {
                    mismatches.Add(new ResolvedMismatch(resolved[0].Id, barcode, channelCode, resolved[0].Value, item.RawValue));
                }

                return false;
            }
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

    private sealed record ResolvedRow
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
    }

    private sealed record SampleLockRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset? ApprovedAt { get; init; }
    }
}
