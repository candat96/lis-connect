using System.Globalization;

namespace Lis.Simulator.Messages;

/// <summary>Một kết quả giả lập: mã kênh đúng như máy thật gửi (HL7: mã OBX-3; ASTM: mã trong R.3; AU: số Online Test No.) và giá trị chữ.</summary>
public sealed record SimResult(string Code, string Value)
{
    /// <summary>Đọc dạng <c>WBC=7.5</c>.</summary>
    public static SimResult Parse(string text)
    {
        var eq = text.IndexOf('=');
        if (eq <= 0 || eq == text.Length - 1)
        {
            throw new ArgumentException($"--result phải dạng MÃ=GIÁ_TRỊ, nhận được: {text}");
        }

        return new SimResult(text[..eq].Trim(), text[(eq + 1)..].Trim());
    }

    internal bool IsNumeric => decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
