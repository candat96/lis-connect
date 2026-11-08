using System.Text;

namespace Lis.Core.Instruments;

/// <summary>Kết quả đoán giao thức từ một đoạn byte nghe được trên cổng.</summary>
/// <param name="Family">Họ giao thức (trùng giá trị cột <c>instrument.protocol_family</c>), null nếu không đoán được.</param>
/// <param name="Explanation">Giải thích cho người dùng, tiếng Việt.</param>
/// <param name="LooksLikeWrongSettings">true khi dữ liệu trông như rác — thường do sai baud / data bits / parity.</param>
public sealed record ProtocolGuess(string? Family, string Explanation, bool LooksLikeWrongSettings = false);

/// <summary>
/// Đoán họ giao thức từ byte thô — dùng ở màn dò cổng COM để người cài đặt biết cổng nào đang
/// nối với loại máy nào. Chỉ là gợi ý theo dấu hiệu khung bản tin (xem <c>docs/drivers/README.md</c>
/// mục 2), không thay cho việc đọc tài liệu máy.
/// </summary>
public static class ProtocolSniffer
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Enq = 0x05;
    private const byte Vt = 0x0B;
    private const byte Fs = 0x1C;

    public static ProtocolGuess Guess(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return new ProtocolGuess(null, "Chưa nhận được byte nào.");
        }

        var text = Encoding.Latin1.GetString(bytes);

        if (bytes.Contains(Vt) && bytes.Contains(Fs))
        {
            return new ProtocolGuess("hl7-mllp", "Khung MLLP <VT>…<FS><CR> — HL7 (Mindray BC-5380, DIRUI BCC-3900…).");
        }

        if (text.Contains("MSH|", StringComparison.Ordinal))
        {
            return new ProtocolGuess("hl7-mllp", "Có segment MSH| — bản tin HL7.");
        }

        if (text.Contains("H|\\^&", StringComparison.Ordinal) || HasAstmFrame(bytes))
        {
            return new ProtocolGuess("astm-e1394", "Khung ASTM E1381/E1394 (<STX>số khung + record H|P|O|R|L).");
        }

        if (bytes[0] == Enq || (bytes.Contains(Enq) && bytes.Count(Enq) >= 2))
        {
            return new ProtocolGuess("astm-e1394", "Máy gửi <ENQ> chờ LIS trả <ACK> — kiểu bắt tay ASTM E1381. Driver chưa trả lời nên máy chỉ gửi ENQ lặp lại.");
        }

        if (text.Contains("Date:", StringComparison.Ordinal) && text.Contains("No.", StringComparison.Ordinal))
        {
            return new ProtocolGuess("dirui-h", "Các dòng Date:/No./chỉ số — dòng chữ cố định DIRUI H (HC-300, H-100…).");
        }

        if (HasAuTextDistinction(bytes))
        {
            return new ProtocolGuess("beckman-au", "<STX> + mã 2 ký tự (D , DB, DE, R …) — có thể giao thức Beckman AU Online.");
        }

        if (LooksLikeGarbage(bytes))
        {
            return new ProtocolGuess(
                null,
                "Nhận được byte nhưng phần lớn là ký tự lạ — thường do sai tốc độ baud, số bit dữ liệu hoặc parity. Thử đổi baud (9600/19200…) hoặc 7 bit.",
                LooksLikeWrongSettings: true);
        }

        if (bytes.Contains(Stx) && bytes.Contains(Etx))
        {
            return new ProtocolGuess(null, "Có khung <STX>…<ETX> nhưng chưa khớp họ nào — đối chiếu tài liệu máy (Sysmex CA, Tosoh, Mindray BC…).");
        }

        return new ProtocolGuess(null, "Văn bản thường (có thể máy gửi nội dung bản in).");
    }

    /// <summary><c>&lt;STX&gt;</c> + số khung 0–7 + chữ record + <c>|</c>.</summary>
    private static bool HasAstmFrame(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == Stx && bytes[i + 1] is >= (byte)'0' and <= (byte)'7'
                && "HPORLQCM"u8.Contains(bytes[i + 2]) && bytes[i + 3] == (byte)'|')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAuTextDistinction(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] != Stx)
            {
                continue;
            }

            var a = bytes[i + 1];
            var b = bytes[i + 2];
            if ((a == (byte)'D' || a == (byte)'R' || a == (byte)'S') && (b == (byte)' ' || b == (byte)'B' || b == (byte)'E' || b == (byte)'H'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeGarbage(ReadOnlySpan<byte> bytes)
    {
        var odd = 0;
        foreach (var b in bytes)
        {
            var isNormal = b is >= 0x20 and < 0x7F or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x0A or 0x0B or 0x0D or 0x15 or 0x17 or 0x1C;
            if (!isNormal)
            {
                odd++;
            }
        }

        return odd * 100 / bytes.Length >= 30;
    }
}
