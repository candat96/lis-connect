using Dapper;
using Lis.Core.Ingest;

namespace Lis.Data.Ingest;

/// <summary>
/// Trả lời máy hỏi chỉ định (host query): với một barcode, máy này cần chạy những kênh nào.
/// </summary>
public interface IOrderQueryService
{
    /// <summary>
    /// Mã kênh của <paramref name="instrumentId"/> ứng với các chỉ số đã chỉ định cho mẫu
    /// <paramref name="barcode"/> mà chưa có kết quả, chỉ những kênh bật "gửi khi máy hỏi".
    /// Không có mẫu / mẫu đã duyệt / không có chỉ định nào trên máy này → danh sách rỗng.
    /// </summary>
    Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default);
}

public sealed class OrderQueryService(Db db) : IOrderQueryService
{
    public async Task<IReadOnlyList<string>> ChannelsToRunAsync(Guid instrumentId, string barcode, CancellationToken ct = default)
    {
        var cleaned = ReadingNormalizer.CleanBarcode(barcode);
        if (cleaned.Length == 0)
        {
            return [];
        }

        await using var connection = await db.OpenAsync(ct);
        var codes = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT ic.channel_code
            FROM sample s
            JOIN order_item oi ON oi.sample_id = s.id
            JOIN instrument_channel ic ON ic.service_id = oi.service_id AND ic.instrument_id = @instrumentId
            LEFT JOIN result r ON r.order_item_id = oi.id
            WHERE s.barcode = @cleaned
              AND s.approved_at IS NULL
              AND ic.send_on_query
              AND (r.id IS NULL OR r.value IS NULL)
            ORDER BY ic.channel_code
            """,
            new { instrumentId, cleaned },
            cancellationToken: ct));
        return codes.ToList();
    }
}
