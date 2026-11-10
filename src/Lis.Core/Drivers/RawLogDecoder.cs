using Lis.Core.Instruments;

namespace Lis.Core.Drivers;

/// <summary>Một bản tin kết quả driver hiểu ra khi giải mã thử log thô.</summary>
public sealed record DecodedReading(DateTimeOffset Timestamp, string Session, DriverReading Reading);

/// <summary>Một câu hỏi chỉ định máy gửi, thấy được khi giải mã thử.</summary>
public sealed record DecodedQuery(DateTimeOffset Timestamp, string Session, string Barcode);

/// <summary>Một ghi chú của driver khi giải mã thử (vd "Checksum sai khung 3").</summary>
public sealed record DecodedNote(DateTimeOffset Timestamp, string Session, string Text);

/// <summary>Kết quả giải mã thử: các bản tin kết quả, câu hỏi chỉ định và ghi chú của driver.</summary>
public sealed record RawLogDecodeResult(
    IReadOnlyList<DecodedReading> Readings,
    IReadOnlyList<DecodedQuery> Queries,
    IReadOnlyList<DecodedNote> Notes);

/// <summary>
/// "Giải mã thử" (thiết kế mục 10): đưa các byte máy gửi (chiều <see cref="RawLogDirection.In"/>)
/// trong một đoạn log thô qua driver để xem driver hiểu ra gì — không ghi CSDL, không gửi gì ra máy.
/// </summary>
/// <remarks>
/// Mỗi khi đổi phiên kết nối thì <see cref="IInstrumentDriver.Reset"/> như lúc chạy thật. Byte driver
/// muốn gửi trả máy (ACK…) bị bỏ qua — máy thật đã nhận câu trả lời của lần chạy gốc. Câu hỏi chỉ
/// định được trả lời "không có" để driver không treo chờ.
/// </remarks>
public static class RawLogDecoder
{
    public static RawLogDecodeResult Decode(IInstrumentDriver driver, IEnumerable<RawLogEntry> entries)
    {
        var readings = new List<DecodedReading>();
        var queries = new List<DecodedQuery>();
        var notes = new List<DecodedNote>();
        string? session = null;
        driver.Reset();

        foreach (var entry in entries)
        {
            if (session is not null && entry.Session != session)
            {
                driver.Reset();
            }

            session = entry.Session;
            if (entry.Direction != RawLogDirection.In || entry.Bytes.Length == 0)
            {
                continue;
            }

            var pending = new Queue<DriverOutput>();
            pending.Enqueue(driver.Receive(entry.Bytes, entry.Timestamp));
            while (pending.TryDequeue(out var output))
            {
                readings.AddRange(output.Readings.Select(r => new DecodedReading(entry.Timestamp, entry.Session, r)));
                notes.AddRange(output.Notes.Select(n => new DecodedNote(entry.Timestamp, entry.Session, n)));
                foreach (var query in output.Queries)
                {
                    queries.Add(new DecodedQuery(entry.Timestamp, entry.Session, query.Barcode));
                    pending.Enqueue(driver.AnswerQuery(query, [], entry.Timestamp));
                }
            }
        }

        return new RawLogDecodeResult(readings, queries, notes);
    }
}
