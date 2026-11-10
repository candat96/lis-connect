using System.Text;
using Lis.Core.Drivers.BeckmanAu;

namespace Lis.Simulator.Messages;

/// <summary>Vị trí mẫu trên máy AU: Rack, Cup, loại mẫu, số mẫu (mặc định đủ dùng khi thử).</summary>
public sealed record AuSamplePosition(string Rack = "1", string Cup = "1", string SampleType = "1", string SampleNo = "1");

/// <summary>
/// Dựng khung "AU Online" của Beckman AU như máy gửi: kết quả (<c>DB</c> → <c>D␣</c>/<c>DQ</c> nhiều
/// khối → <c>DE</c>) và câu hỏi chỉ định (<c>R␣</c>). Bố cục cột cố định theo
/// <see cref="BeckmanAuOptions"/> — phải trùng cấu hình driver. Lớp thuần, không IO.
/// </summary>
public static class AuMessageBuilder
{
    public static IReadOnlyList<byte[]> BuildResultFrames(
        BeckmanAuOptions o, string barcode, IReadOnlyList<SimResult> results, bool isQc, AuSamplePosition? position = null)
    {
        var head = Head(o, barcode, position ?? new AuSamplePosition());
        var code = isQc ? "DQ" : "D ";
        var sysNo = o.SystemNo ? "00" : "";

        // Mỗi khối: mã + System No + phần cố định + Dummy 4 ô + số khối (1 ký tự) + phần biến đổi.
        var prefixLength = code.Length + sysNo.Length + head.Length + 4 + 1;
        var items = results.Select(r => Item(o, r)).ToList();
        var firstPrefix = isQc
            ? new string(' ', (o.LoadingTime ? 14 : 0) + o.ControlNoDigits)
            : new string(' ', PatientBlockLength(o) + (o.LoadingTime ? 14 : 0));
        var itemsPerBlock = Math.Max(1, (o.TextLength - 2 - (o.Bcc ? 1 : 0) - prefixLength - firstPrefix.Length) / ItemLength(o));

        var blocks = new List<string>();
        for (var i = 0; i < Math.Max(items.Count, 1); i += itemsPerBlock)
        {
            blocks.Add(string.Concat(items.Skip(i).Take(itemsPerBlock)));
        }

        if (blocks.Count > 10)
        {
            throw new ArgumentException($"Quá nhiều kết quả: cần {blocks.Count} khối, bản tin AU chỉ có tối đa 10 khối (0–8 và E)");
        }

        var frames = new List<byte[]> { BuildFrame(o, "DB" + sysNo) };
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = i == blocks.Count - 1 ? 'E' : (char)('0' + i);
            var variable = (i == 0 ? firstPrefix : "") + blocks[i];
            frames.Add(BuildFrame(o, code + sysNo + head + "    " + block + variable));
        }

        frames.Add(BuildFrame(o, "DE" + sysNo));
        return frames;
    }

    /// <summary>Câu hỏi chỉ định <c>R␣</c>: mã + Rack + Cup + loại mẫu + số mẫu + Sample ID (căn phải).</summary>
    public static byte[] BuildQueryFrame(BeckmanAuOptions o, string barcode, AuSamplePosition? position = null) =>
        BuildFrame(o, "R " + (o.SystemNo ? "00" : "") + Head(o, barcode, position ?? new AuSamplePosition()));

    /// <summary>Đóng khung: Start + văn bản + End (+ BCC nếu bật).</summary>
    public static byte[] BuildFrame(BeckmanAuOptions o, string text)
    {
        var body = Encoding.Latin1.GetBytes(text);
        var frame = new List<byte>(body.Length + 3) { o.StartCode };
        frame.AddRange(body);
        frame.Add(o.EndCode);
        if (o.Bcc)
        {
            byte bcc = o.EndCode;
            foreach (var b in body)
            {
                bcc ^= b;
            }

            frame.Add(bcc);
        }

        return [.. frame];
    }

    /// <summary>Vị trí khối (ký tự 0–9/E) trong khung bản tin LIS gửi (<c>S␣</c>): sau mã (2) + phần cố định + 4 ô Dummy.</summary>
    public static int ReplyBlockIndex(BeckmanAuOptions o) =>
        2 + o.RackDigits + (o.CupPosition ? 2 : 0) + 1 + 4 + o.SampleIdLength + 4;

    // Hai độ dài này là internal trong Lis.Core nên tính lại đúng công thức của driver.
    private static int PatientBlockLength(BeckmanAuOptions o) =>
        (o.Sex ? 1 : 0) + (o.YearAge ? 3 : 0) + (o.MonthAge ? 2 : 0) + o.PatientInfoLengths.Sum();

    private static int ItemLength(BeckmanAuOptions o) => o.TestNoDigits + o.ReagentInfoLength + o.ResultDigits + o.FlagLength;

    private static string Head(BeckmanAuOptions o, string barcode, AuSamplePosition p)
    {
        if (barcode.Length > o.SampleIdLength)
        {
            throw new ArgumentException($"Barcode '{barcode}' dài hơn Sample ID của máy ({o.SampleIdLength} ký tự) — chỉnh --sample-id-length");
        }

        var sb = new StringBuilder();
        sb.Append(Digits(p.Rack, o.RackDigits));
        if (o.CupPosition)
        {
            sb.Append(Digits(p.Cup, 2));
        }

        sb.Append(p.SampleType.PadLeft(1)[..1]);
        sb.Append(Digits(p.SampleNo, 4));
        sb.Append(barcode.PadLeft(o.SampleIdLength));
        return sb.ToString();
    }

    private static string Digits(string value, int width) =>
        width == 0 ? "" : value.PadLeft(width, '0')[^width..];

    private static string Item(BeckmanAuOptions o, SimResult result)
    {
        if (result.Code.Length == 0 || result.Code.Length > o.TestNoDigits || !result.Code.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"Mã kênh AU '{result.Code}' phải là số Online Test No. tối đa {o.TestNoDigits} chữ số");
        }

        if (result.Value.Length > o.ResultDigits)
        {
            throw new ArgumentException($"Giá trị '{result.Value}' dài hơn {o.ResultDigits} ký tự (Result Digit)");
        }

        return result.Code.PadLeft(o.TestNoDigits, '0')
            + new string(' ', o.ReagentInfoLength)
            + result.Value.PadLeft(o.ResultDigits)
            + new string(' ', o.FlagLength);
    }
}

/// <summary>Cắt luồng byte LIS gửi về thành các khung AU (Start…End[BCC]) và đếm ACK/NAK lẻ. Thuần logic.</summary>
public sealed class AuFrameReader(BeckmanAuOptions options)
{
    private readonly List<byte> _frame = [];
    private bool _inFrame;
    private bool _awaitBcc;

    /// <summary>Văn bản các khung đã nhận (không gồm Start/End/BCC).</summary>
    public List<string> Frames { get; } = [];

    public int AckCount { get; private set; }

    public int NakCount { get; private set; }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (_awaitBcc)
            {
                _awaitBcc = false;
                Frames.Add(Encoding.Latin1.GetString(_frame.ToArray()));
                _frame.Clear();
                continue;
            }

            if (!_inFrame)
            {
                if (b == options.StartCode)
                {
                    _inFrame = true;
                    _frame.Clear();
                }
                else if (b == 0x06)
                {
                    AckCount++;
                }
                else if (b == 0x15)
                {
                    NakCount++;
                }

                continue;
            }

            if (b == options.EndCode || b == 0x17)
            {
                _inFrame = false;
                if (options.Bcc)
                {
                    _awaitBcc = true;
                }
                else
                {
                    Frames.Add(Encoding.Latin1.GetString(_frame.ToArray()));
                    _frame.Clear();
                }
            }
            else
            {
                _frame.Add(b);
            }
        }
    }
}
