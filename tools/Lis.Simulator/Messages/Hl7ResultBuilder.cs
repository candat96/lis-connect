using System.Globalization;
using System.Text;

namespace Lis.Simulator.Messages;

/// <summary>Dựng bản tin HL7 ORU^R01 như máy huyết học (Mindray) gửi, bọc MLLP. Lớp thuần, không IO.</summary>
public static class Hl7ResultBuilder
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;

    /// <summary>Văn bản ORU^R01 (các segment ngăn bằng CR). QC: MSH-11 = Q.</summary>
    public static string BuildOru(string barcode, IReadOnlyList<SimResult> results, DateTimeOffset now, int controlId, bool isQc)
    {
        var stamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append("MSH|^~\\&|SIM||LIS||").Append(stamp).Append("||ORU^R01|")
            .Append(controlId.ToString(CultureInfo.InvariantCulture)).Append('|').Append(isQc ? 'Q' : 'P').Append("|2.3.1||||||UNICODE\r");
        text.Append("PID|1\r");
        text.Append("PV1|1\r");
        text.Append("OBR|1||").Append(Escape(barcode)).Append("|00001^Automated Count^99MRC|||").Append(stamp).Append('\r');
        var index = 1;
        foreach (var result in results)
        {
            var code = Escape(result.Code);
            text.Append("OBX|").Append(index++.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(result.IsNumeric ? "NM" : "ST").Append('|')
                .Append(code).Append('^').Append(code).Append("^99MRC||").Append(Escape(result.Value)).Append('\r');
        }

        return text.ToString();
    }

    /// <summary>Bọc MLLP: <c>&lt;VT&gt;văn bản&lt;FS&gt;&lt;CR&gt;</c>.</summary>
    public static byte[] Frame(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var frame = new byte[body.Length + 3];
        frame[0] = StartBlock;
        body.CopyTo(frame, 1);
        frame[^2] = EndBlock;
        frame[^1] = 0x0D;
        return frame;
    }

    /// <summary>Lấy mã xác nhận (MSA-1: AA/AE/AR…) từ khung MLLP LIS trả về; null nếu không thấy MSA.</summary>
    public static string? ParseAckCode(ReadOnlySpan<byte> frame)
    {
        var text = Encoding.UTF8.GetString(frame);
        foreach (var line in text.Split(['\r', '\n', '\u000B', '\u001C'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("MSA|", StringComparison.Ordinal))
            {
                var parts = line.Split('|');
                return parts.Length > 1 ? parts[1] : null;
            }
        }

        return null;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\E\\").Replace("|", "\\F\\").Replace("^", "\\S\\").Replace("~", "\\R\\").Replace("&", "\\T\\");
}
