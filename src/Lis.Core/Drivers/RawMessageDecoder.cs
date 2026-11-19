using System.Reflection;

namespace Lis.Core.Drivers;

/// <summary>Kết quả giải mã một bản tin thô: các bản tin kết quả, mã vạch câu hỏi chỉ định, ghi chú của driver và cờ bỏ dở.</summary>
/// <param name="Abandoned">Có ít nhất một ranh giới "bỏ dở" (hết giờ, bắt đầu lại giữa chừng, khung quá cỡ…).</param>
public sealed record RawMessageDecodeResult(
    IReadOnlyList<DriverReading> Readings,
    IReadOnlyList<string> QueryBarcodes,
    IReadOnlyList<string> Notes,
    bool Abandoned);

/// <summary>
/// Giải mã MỘT bản tin thô (một lượt truyền đã được agent cắt theo <see cref="DriverBoundary"/>) bằng một
/// driver mới tinh — thuần logic, không ghi CSDL, không gửi gì ra máy (thiết kế mục 5.D và mục 9).
/// </summary>
/// <remarks>
/// Cách chạy: <see cref="IInstrumentDriver.Reset"/> → <see cref="IInstrumentDriver.Receive"/> toàn bộ byte →
/// mỗi câu hỏi chỉ định được trả lời "không có" (<see cref="IInstrumentDriver.AnswerQuery"/> với danh sách kênh
/// rỗng) để driver không treo chờ → một lần <see cref="IInstrumentDriver.Tick"/> sau 5 phút để xả trạng thái chờ.
/// Byte driver muốn gửi trả máy (<see cref="DriverOutput.Send"/>) bị bỏ qua. Khác
/// <see cref="RawLogDecoder"/> (giải mã cả đoạn log nhiều phiên) ở chỗ chỉ có một bản tin và có Tick cuối.
/// </remarks>
public static class RawMessageDecoder
{
    private static readonly TimeSpan FinalTick = TimeSpan.FromMinutes(5);

    /// <summary>Phiên bản bộ giải mã = InformationalVersion của Lis.Core; lưu kèm mỗi lần giải mã để truy vết.</summary>
    public static string Version { get; } =
        typeof(RawMessageDecoder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RawMessageDecoder).Assembly.GetName().Version?.ToString()
        ?? "không rõ";

    public static RawMessageDecodeResult Decode(IInstrumentDriver freshDriver, ReadOnlySpan<byte> bytes, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(freshDriver);

        var readings = new List<DriverReading>();
        var queries = new List<string>();
        var notes = new List<string>();
        var abandoned = false;

        void Absorb(DriverOutput output)
        {
            var pending = new Queue<DriverOutput>();
            pending.Enqueue(output);
            while (pending.TryDequeue(out var current))
            {
                readings.AddRange(current.Readings);
                notes.AddRange(current.Notes);
                abandoned |= current.Boundaries.Any(b => b.Abandoned);
                foreach (var query in current.Queries)
                {
                    queries.Add(query.Barcode);
                    pending.Enqueue(freshDriver.AnswerQuery(query, [], receivedAt));
                }
            }
        }

        freshDriver.Reset();
        Absorb(freshDriver.Receive(bytes, receivedAt));
        Absorb(freshDriver.Tick(receivedAt + FinalTick));
        return new RawMessageDecodeResult(readings, queries, notes, abandoned);
    }
}
