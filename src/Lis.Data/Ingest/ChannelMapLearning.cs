using Dapper;
using Lis.Core;
using Lis.Core.Instruments;
using Lis.Data.Instruments;

namespace Lis.Data.Ingest;

/// <summary>Một chỉ số ứng viên cho mã kênh (mã + tên để hiển thị).</summary>
public sealed record ServiceRef(Guid Id, string Code, string Name);

/// <summary>Gợi ý map cho một mã kênh máy đã gửi mà chưa map.</summary>
public sealed record ChannelMapHint(string ChannelCode, IReadOnlyList<ServiceRef> Candidates, int SampleCount)
{
    public bool IsCertain => Candidates.Count == 1;
}

/// <summary>Gợi ý map kênh từ barcode và xác nhận gợi ý. Bản thật: <see cref="ChannelMapLearning"/>.</summary>
public interface IChannelMapLearning
{
    /// <summary>Gợi ý cho mọi mã kênh chưa map của máy (xem <see cref="ChannelMapInference"/>).</summary>
    Task<IReadOnlyList<ChannelMapHint>> SuggestAsync(Guid instrumentId);

    /// <summary>
    /// Lưu map <paramref name="channelCode"/> → <paramref name="serviceId"/>, rồi đưa các kết quả
    /// cũ của mã kênh này (đang nằm chờ vì chưa map) vào đúng chỉ số của mẫu. Trả về số kết quả đã đưa vào.
    /// </summary>
    Task<int> ConfirmAsync(Guid instrumentId, string channelCode, Guid serviceId, Guid actorId);
}

public sealed class ChannelMapLearning(Db db, IInstrumentStore instruments, IUnmatchedService unmatched) : IChannelMapLearning
{
    public async Task<IReadOnlyList<ChannelMapHint>> SuggestAsync(Guid instrumentId)
    {
        await using var connection = await db.OpenAsync();

        var rows = (await connection.QueryAsync<(string ChannelCode, string Barcode, string Value)>(
            """
            SELECT u.channel_code, u.barcode, u.value
            FROM unmatched_result u
            WHERE u.instrument_id = @instrumentId AND u.reason = 'kenh-chua-map' AND u.resolved_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = u.instrument_id AND ic.channel_code = u.channel_code)
            """,
            new { instrumentId })).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var barcodes = rows.Select(r => r.Barcode).Distinct().ToArray();

        // Chỉ số lá được chỉ định cho từng barcode mà máy này chưa map.
        var candidateRows = await connection.QueryAsync<(string Barcode, Guid ServiceId, string Code, string Name)>(
            """
            SELECT s.barcode, sv.id, sv.code, sv.name
            FROM sample s
            JOIN order_item oi ON oi.sample_id = s.id
            JOIN service sv ON sv.id = oi.service_id AND NOT sv.is_panel
            WHERE s.barcode = ANY(@barcodes)
              AND NOT EXISTS (SELECT 1 FROM instrument_channel ic WHERE ic.instrument_id = @instrumentId AND ic.service_id = sv.id)
            """,
            new { barcodes, instrumentId });

        var byBarcode = candidateRows.GroupBy(r => r.Barcode).ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(r => r.ServiceId).ToHashSet());
        var services = candidateRows.GroupBy(r => r.ServiceId).ToDictionary(g => g.Key, g => new ServiceRef(g.Key, g.First().Code, g.First().Name));

        var serviceIds = services.Keys.ToArray();
        var ranges = (await connection.QueryAsync<(Guid ServiceId, decimal? Low, decimal? High)>(
                "SELECT service_id, min(low), max(high) FROM reference_range WHERE service_id = ANY(@serviceIds) GROUP BY service_id",
                new { serviceIds }))
            .ToDictionary(r => r.ServiceId, r => new ValueRange(r.Low, r.High));

        var observations = rows.Select(r => new ChannelObservation(
            r.ChannelCode,
            r.Barcode,
            DecimalInput.TryParse(r.Value.TrimStart('<', '>', '=', ' '), out var value) ? value : null,
            byBarcode.GetValueOrDefault(r.Barcode) ?? new HashSet<Guid>()));

        return ChannelMapInference.Infer(observations, ranges)
            .Select(s => new ChannelMapHint(s.ChannelCode, s.Candidates.Select(id => services[id]).ToList(), s.SampleCount))
            .ToList();
    }

    public async Task<int> ConfirmAsync(Guid instrumentId, string channelCode, Guid serviceId, Guid actorId)
    {
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, channelCode, serviceId, null, null, false, true));

        List<(Guid UnmatchedId, Guid OrderItemId)> pending;
        await using (var connection = await db.OpenAsync())
        {
            pending = (await connection.QueryAsync<(Guid, Guid)>(
                """
                SELECT u.id, oi.id
                FROM unmatched_result u
                JOIN sample s ON s.barcode = u.barcode
                JOIN order_item oi ON oi.sample_id = s.id AND oi.service_id = @serviceId
                WHERE u.instrument_id = @instrumentId AND u.channel_code = @channelCode
                  AND u.reason = 'kenh-chua-map' AND u.resolved_at IS NULL
                ORDER BY u.received_at
                """,
                new { instrumentId, channelCode, serviceId })).ToList();
        }

        var applied = 0;
        foreach (var (unmatchedId, orderItemId) in pending)
        {
            try
            {
                await unmatched.AssignAsync(unmatchedId, orderItemId, actorId);
                applied++;
            }
            catch (InvalidOperationException)
            {
                // Dòng đã được xử lý ở chỗ khác — bỏ qua.
            }
        }

        return applied;
    }
}
