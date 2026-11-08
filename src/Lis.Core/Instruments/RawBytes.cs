using System.Text;

namespace Lis.Core.Instruments;

/// <summary>
/// Hiển thị byte thô nhận từ máy cho người đọc: ký tự điều khiển dạng <c>&lt;STX&gt;</c>,
/// <c>&lt;ETX&gt;</c>, <c>&lt;ACK&gt;</c>…, byte ngoài ASCII dạng <c>&lt;xAB&gt;</c>, xuống dòng sau
/// <c>&lt;CR&gt;</c>/<c>&lt;LF&gt;</c> để dễ đọc từng record.
/// </summary>
public static class RawBytes
{
    private static readonly string[] ControlNames =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    public static string ToReadable(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length + 16);
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0x20)
            {
                sb.Append('<').Append(ControlNames[b]).Append('>');

                // Xuống dòng sau LF, hoặc sau CR không đi kèm LF — mỗi record ASTM/HL7 một dòng.
                if (b == 0x0A || (b == 0x0D && (i + 1 >= bytes.Length || bytes[i + 1] != 0x0A)))
                {
                    sb.Append('\n');
                }
            }
            else if (b == 0x7F)
            {
                sb.Append("<DEL>");
            }
            else if (b > 0x7F)
            {
                sb.Append("<x").Append(b.ToString("X2")).Append('>');
            }
            else
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString();
    }

    /// <summary>Dạng hex, 16 byte mỗi dòng, kèm cột ASCII.</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            sb.Append(offset.ToString("X6")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2") + " " : "   ");
            }

            sb.Append(' ');
            foreach (var b in line)
            {
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
