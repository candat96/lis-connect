using Dapper;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Data.Results;

namespace Lis.Data.Ingest;

/// <summary>Một dòng Chưa ghép đang mở. <see cref="ServiceCode"/>/<see cref="ServiceName"/> có khi kênh đã map (lý do "chưa có chỉ định").</summary>
public sealed record UnmatchedItem(
    Guid Id,
    string InstrumentName,
    string Barcode,
    string ChannelCode,
    string Value,
    string? Unit,
    string Reason,
    DateTimeOffset ReceivedAt,
    string? ServiceCode,
    string? ServiceName);

/// <summary>Một chỉ số lá của mẫu để chọn khi gán tay, kèm giá trị hiện có (nếu đã có).</summary>
public sealed record SampleLeafItem(Guid OrderItemId, string ServiceCode, string ServiceName, string? CurrentValue);

/// <summary>Nghiệp vụ màn Chưa ghép. Cài đặt thật là <see cref="UnmatchedService"/>; kiểm thử ViewModel dùng bản giả.</summary>
public interface IUnmatchedService
{
    /// <summary>Các dòng chưa xử lý nhận trong khoảng ngày [<paramref name="from"/>, <paramref name="to"/>] theo giờ máy, mới nhất trước.</summary>
    Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to);

    /// <summary>Các chỉ số lá của mẫu có barcode này (so khớp chính xác). Không có mẫu → <see cref="InvalidOperationException"/>.</summary>
    Task<IReadOnlyList<SampleLeafItem>> SampleItemsAsync(string barcode);

    /// <summary>Gán giá trị của dòng Chưa ghép vào một chỉ số của mẫu (qua đường tính giá trị chung), đánh dấu đã xử lý rồi thử tự duyệt.</summary>
    Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid actorId);

    /// <summary>Bỏ qua một dòng với lý do bắt buộc.</summary>
    Task DismissAsync(Guid unmatchedId, Guid actorId, string reason);
}

/// <summary>
/// Xử lý bảng <c>unmatched_result</c>: liệt kê, gán tay vào mẫu, bỏ qua. Gán tay đi qua cùng
/// đường ghi với máy gửi (<see cref="InstrumentValuePipeline"/>): dùng map kênh nếu đã có, không
/// thì số lẻ của chỉ số và không hệ số; kết quả đã duyệt thành bản chờ thay thế.
/// </summary>
public sealed class UnmatchedService(Db db, TimeProvider clock) : IUnmatchedService
{
    private const string ReasonAssigned = "Gán tay từ Chưa ghép";
    private const string AlreadyResolved = "Dòng này đã được xử lý";

    private readonly ResultWriter _writer = new(db);
    private readonly ResultService _results = new(db, clock);

    public async Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to)
    {
        var (fromUtc, toUtcExclusive) = LocalDay.UtcRange(clock, from, to);

        await using var connection = await db.OpenAsync();
        var rows = await connection.QueryAsync<OpenRow>(
            """
            SELECT u.id, i.name AS instrument_name, u.barcode, u.channel_code, u.value, u.unit, u.reason, u.received_at,
                   sv.code AS service_code, sv.name AS service_name
            FROM unmatched_result u
            JOIN instrument i ON i.id = u.instrument_id
            LEFT JOIN instrument_channel ic ON ic.instrument_id = u.instrument_id AND ic.channel_code = u.channel_code
            LEFT JOIN service sv ON sv.id = ic.service_id
            WHERE u.resolved_at IS NULL AND u.received_at >= @fromUtc AND u.received_at < @toUtcExclusive
            ORDER BY u.received_at DESC, u.channel_code
            """,
            new { fromUtc, toUtcExclusive });
        return rows.Select(row => row.ToItem()).ToList();
    }

    public async Task<IReadOnlyList<SampleLeafItem>> SampleItemsAsync(string barcode)
    {
        var cleaned = ReadingNormalizer.CleanBarcode(barcode);

        await using var connection = await db.OpenAsync();
        var sampleId = await connection.ExecuteScalarAsync<Guid?>("SELECT id FROM sample WHERE barcode = @cleaned", new { cleaned })
            ?? throw new InvalidOperationException($"Không tìm thấy mẫu có barcode {cleaned}");

        var rows = await connection.QueryAsync<LeafRow>(
            """
            SELECT oi.id AS order_item_id, sv.code AS service_code, sv.name AS service_name, r.value AS current_value
            FROM order_item oi
            JOIN service sv ON sv.id = oi.service_id AND sv.is_panel = false
            LEFT JOIN result r ON r.order_item_id = oi.id
            WHERE oi.sample_id = @sampleId
            ORDER BY coalesce(sv.print_order, 0), sv.code
            """,
            new { sampleId });
        return rows.Select(row => new SampleLeafItem(row.OrderItemId, row.ServiceCode, row.ServiceName, row.CurrentValue)).ToList();
    }

    public async Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid actorId)
    {
        await db.InTransactionAsync(async (connection, transaction) =>
        {
            var unmatched = await connection.QuerySingleOrDefaultAsync<UnmatchedRow>(
                "SELECT id, instrument_id, channel_code, value, unit, instrument_flag, raw_ref, resolved_at FROM unmatched_result WHERE id = @unmatchedId FOR UPDATE",
                new { unmatchedId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng Chưa ghép");

            if (unmatched.ResolvedAt is not null)
            {
                throw new InvalidOperationException(AlreadyResolved);
            }

            // Khóa mẫu TRƯỚC, bằng một câu lệnh riêng — không gộp vào câu SELECT đọc chỉ định:
            // Postgres có thể khóa order_item trước sample khi FOR UPDATE OF liệt kê cả hai trong
            // cùng một JOIN (JOIN bắt đầu từ order_item), đảo ngược thứ tự khóa dùng chung toàn hệ
            // thống (mẫu → chỉ định → kết quả) và có thể gây deadlock với đường máy gửi.
            var sampleId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT sample_id FROM order_item WHERE id = @orderItemId", new { orderItemId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng chỉ định");

            var sampleApprovedAt = await ResultWriter.LockSampleAsync(connection, transaction, sampleId);

            var item = await connection.QuerySingleOrDefaultAsync<ItemContext>(
                $"""
                {ResultWriter.ItemContextSelect("LEFT JOIN")}
                WHERE oi.id = @orderItemId
                FOR UPDATE OF oi, s
                """,
                new { orderItemId }, transaction)
                ?? throw new InvalidOperationException("Không tìm thấy dòng chỉ định");

            var channel = await InstrumentStore.FindChannelAsync(connection, transaction, unmatched.InstrumentId, unmatched.ChannelCode);
            var reading = new ReadingItem(unmatched.ChannelCode, unmatched.Value, unmatched.Unit, unmatched.InstrumentFlag);

            await InstrumentValuePipeline.WriteAsync(
                connection, transaction, _writer, clock, item, sampleApprovedAt, channel, unmatched.InstrumentId, reading, unmatched.RawRef,
                ReasonAssigned, ReasonAssigned, actorId);

            await connection.ExecuteAsync(
                "UPDATE unmatched_result SET resolved_at = @now, resolved_by = @actorId, resolved_item_id = @orderItemId WHERE id = @unmatchedId",
                new { unmatchedId, now = clock.GetUtcNow(), actorId, orderItemId }, transaction);

            await _results.TryAutoApproveAsync(connection, transaction, item.SampleId);
            return true;
        });
    }

    public async Task DismissAsync(Guid unmatchedId, Guid actorId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Nhập lý do bỏ qua");
        }

        await using var connection = await db.OpenAsync();
        var updated = await connection.ExecuteAsync(
            """
            UPDATE unmatched_result SET resolved_at = @now, resolved_by = @actorId, dismiss_reason = @reason, resolved_item_id = NULL
            WHERE id = @unmatchedId AND resolved_at IS NULL
            """,
            new { unmatchedId, now = clock.GetUtcNow(), actorId, reason = reason.Trim() });

        if (updated == 0)
        {
            throw new InvalidOperationException(AlreadyResolved);
        }
    }

    private sealed record OpenRow
    {
        public Guid Id { get; init; }
        public string InstrumentName { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string ChannelCode { get; init; } = "";
        public string Value { get; init; } = "";
        public string? Unit { get; init; }
        public string Reason { get; init; } = "";
        public DateTimeOffset ReceivedAt { get; init; }
        public string? ServiceCode { get; init; }
        public string? ServiceName { get; init; }

        public UnmatchedItem ToItem() => new(Id, InstrumentName, Barcode, ChannelCode, Value, Unit, Reason, ReceivedAt, ServiceCode, ServiceName);
    }

    private sealed record LeafRow
    {
        public Guid OrderItemId { get; init; }
        public string ServiceCode { get; init; } = "";
        public string ServiceName { get; init; } = "";
        public string? CurrentValue { get; init; }
    }

    private sealed record UnmatchedRow
    {
        public Guid Id { get; init; }
        public Guid InstrumentId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string Value { get; init; } = "";
        public string? Unit { get; init; }
        public string? InstrumentFlag { get; init; }
        public string? RawRef { get; init; }
        public DateTimeOffset? ResolvedAt { get; init; }
    }
}
