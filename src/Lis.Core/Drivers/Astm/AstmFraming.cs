using System.Text;

namespace Lis.Core.Drivers.Astm;

/// <summary>
/// Ký tự điều khiển và cách đóng khung của ASTM E1381 (CLSI LIS1-A):
/// <c>&lt;STX&gt; FN text &lt;ETB|ETX&gt; C1 C2 &lt;CR&gt;&lt;LF&gt;</c>, checksum = tổng byte từ FN đến
/// hết ETB/ETX, mod 256, 2 ký tự hex in hoa (xem <c>docs/drivers/astm-e1394.md</c> mục 3).
/// </summary>
public static class AstmFraming
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Eot = 0x04;
    public const byte Enq = 0x05;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Etb = 0x17;
    public const byte Cr = 0x0D;
    public const byte Lf = 0x0A;

    /// <summary>Độ dài tối đa phần text của một frame.</summary>
    public const int MaxFrameText = 240;

    /// <summary>
    /// Mã hóa chuyển byte ↔ chữ ở tầng record: Latin-1 ánh xạ 1-1 mọi byte 0–255 nên không bao giờ
    /// làm hỏng dữ liệu máy gửi (máy có thể gửi byte &gt; 0x7F trong tên/ghi chú).
    /// </summary>
    public static readonly Encoding TextEncoding = Encoding.Latin1;

    /// <summary>Checksum của phần <paramref name="body"/> = FN + text + ETB/ETX.</summary>
    public static string Checksum(ReadOnlySpan<byte> body)
    {
        var sum = 0;
        foreach (var b in body)
        {
            sum += b;
        }

        return (sum & 0xFF).ToString("X2");
    }

    /// <summary>
    /// Tách một record (chưa có CR cuối) thành các frame gửi đi; frame đầu mang số
    /// <paramref name="firstFrameNumber"/>, mỗi frame sau tăng 1 (mod 8). Trả về số frame kế tiếp
    /// qua <paramref name="nextFrameNumber"/>.
    /// </summary>
    public static List<byte[]> BuildFrames(string record, int firstFrameNumber, out int nextFrameNumber)
    {
        var text = TextEncoding.GetBytes(record + "\r");
        var frames = new List<byte[]>();
        var fn = firstFrameNumber;
        for (var offset = 0; offset < text.Length; offset += MaxFrameText)
        {
            var length = Math.Min(MaxFrameText, text.Length - offset);
            var last = offset + length >= text.Length;
            frames.Add(BuildFrame(fn, text.AsSpan(offset, length), last));
            fn = (fn + 1) % 8;
        }

        nextFrameNumber = fn;
        return frames;
    }

    /// <summary>Đóng khung một đoạn text đã có sẵn CR (nếu là cuối record).</summary>
    public static byte[] BuildFrame(int frameNumber, ReadOnlySpan<byte> text, bool last)
    {
        var frame = new byte[text.Length + 7];
        frame[0] = Stx;
        frame[1] = (byte)('0' + frameNumber);
        text.CopyTo(frame.AsSpan(2));
        frame[2 + text.Length] = last ? Etx : Etb;
        var checksum = Checksum(frame.AsSpan(1, text.Length + 2));
        frame[3 + text.Length] = (byte)checksum[0];
        frame[4 + text.Length] = (byte)checksum[1];
        frame[5 + text.Length] = Cr;
        frame[6 + text.Length] = Lf;
        return frame;
    }
}
