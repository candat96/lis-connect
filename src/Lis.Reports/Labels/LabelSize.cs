using System.Globalization;

namespace Lis.Reports.Labels;

/// <summary>
/// Kích thước tem (mm). Lưu ở bảng <c>setting</c> khóa <c>label.size</c> dạng <c>"50x25"</c>
/// (xem <see cref="ToSetting"/>/<see cref="FromSetting"/>); mặc định 50 × 25 mm.
/// </summary>
public sealed record LabelSize(double WidthMm, double HeightMm)
{
    public static readonly LabelSize Default = new(50, 25);

    /// <summary>
    /// Đọc giá trị đã lưu (vd <c>"50x25"</c>, chấp nhận dấu phẩy thập phân). Thiếu hoặc sai
    /// dạng thì trả về <see cref="Default"/> — tem vẫn in được thay vì chặn người dùng.
    /// </summary>
    public static LabelSize FromSetting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var parts = text.Split(['x', 'X', '×'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryParseMm(parts[0], out var width) || !TryParseMm(parts[1], out var height))
        {
            return Default;
        }

        return new LabelSize(width, height);
    }

    /// <summary>Chuỗi để lưu vào <c>setting</c>, luôn dùng dấu chấm thập phân: <c>"50x25"</c>, <c>"40.5x20"</c>.</summary>
    public string ToSetting() =>
        $"{WidthMm.ToString("0.##", CultureInfo.InvariantCulture)}x{HeightMm.ToString("0.##", CultureInfo.InvariantCulture)}";

    private static bool TryParseMm(string text, out double value)
    {
        value = 0;
        var normalized = text.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && value > 0
            && double.IsFinite(value);
    }
}
