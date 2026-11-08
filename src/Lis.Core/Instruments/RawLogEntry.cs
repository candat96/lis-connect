using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lis.Core.Instruments;

/// <summary>Chiều của một bản ghi log thô.</summary>
public enum RawLogDirection
{
    /// <summary>Máy → LIS.</summary>
    In,

    /// <summary>LIS → máy.</summary>
    Out,

    /// <summary>Sự kiện kết nối (mở cổng, máy nối vào, mất kết nối, lỗi…) — không có byte.</summary>
    Event,
}

/// <summary>
/// Một bản ghi log thô: mọi byte đi/về giữa LIS và một máy, ghi nguyên văn, kèm thời điểm đến
/// mili-giây, chiều và phiên kết nối (xem thiết kế mục 10, "Log thô").
/// </summary>
public sealed record RawLogEntry(DateTimeOffset Timestamp, RawLogDirection Direction, string Session, byte[] Bytes, string? Note = null)
{
    /// <summary>Đúng một byte ACK (0x06) — các ACK lẻ có thể ẩn khi xem.</summary>
    public bool IsLoneAck => Bytes.Length == 1 && Bytes[0] == 0x06;
}

/// <summary>
/// Định dạng tệp <c>.listrace</c>: JSON lines, mỗi dòng một <see cref="RawLogEntry"/>:
/// <c>{"t":"2026-11-08T09:15:02.123+07:00","d":"in","s":"phien-1","hex":"0231487C…","note":null}</c>.
/// Byte lưu dạng hex để giữ nguyên ký tự điều khiển và byte ngoài ASCII.
/// </summary>
public static class RawLogFormat
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Giữ nguyên "+07:00" và chữ tiếng Việt trong ghi chú để mở tệp bằng Notepad vẫn đọc được.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(RawLogEntry entry) =>
        JsonSerializer.Serialize(
            new Line(
                entry.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                entry.Direction switch { RawLogDirection.In => "in", RawLogDirection.Out => "out", _ => "event" },
                entry.Session,
                entry.Bytes.Length == 0 ? null : Convert.ToHexString(entry.Bytes),
                entry.Note),
            Options);

    /// <summary>Đọc một dòng. Dòng hỏng (ghi dở khi mất điện…) → null, bên gọi bỏ qua.</summary>
    public static RawLogEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Line>(line, Options);
            if (parsed is null || !DateTimeOffset.TryParse(parsed.T, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            {
                return null;
            }

            var direction = parsed.D switch { "in" => RawLogDirection.In, "out" => RawLogDirection.Out, _ => RawLogDirection.Event };
            var bytes = string.IsNullOrEmpty(parsed.Hex) ? [] : Convert.FromHexString(parsed.Hex);
            return new RawLogEntry(timestamp, direction, parsed.S ?? "", bytes, parsed.Note);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed record Line(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("d")] string D,
        [property: JsonPropertyName("s")] string? S,
        [property: JsonPropertyName("hex")] string? Hex,
        [property: JsonPropertyName("note")] string? Note);
}
