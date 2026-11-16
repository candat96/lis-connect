using System.Globalization;
using System.Text.RegularExpressions;

namespace Lis.Core.Results;

/// <summary>
/// Giá trị kết quả xét nghiệm đã phân tích từ chuỗi thô do máy gửi hoặc
/// kỹ thuật viên nhập tay. Luôn giữ chuỗi gốc (<see cref="Raw"/>) để hiển thị
/// và in ấn; <see cref="Number"/> chỉ dùng để so ngưỡng/tính cờ.
/// </summary>
public sealed record ResultValue(string Raw, decimal? Number, string? Comparator, bool IsEmpty)
{
    /// <summary>
    /// Mẫu so khớp hiệu giá dạng "a:b" hoặc "a/b". Chỉ chữ số ASCII — <c>\d</c> của .NET khớp cả
    /// chữ số Unicode khác (vd Ả Rập-Ấn) mà <see cref="decimal"/> không đọc được.
    /// </summary>
    private static readonly Regex TiterPattern = new(@"^([0-9]+)\s*[:/]\s*([0-9]+)$", RegexOptions.Compiled);

    /// <summary>
    /// Phân tích chuỗi kết quả thô. Chuỗi rỗng, null, hoặc không khớp dạng số/hiệu
    /// giá nào (ví dụ ký hiệu lỗi của máy "***.*", "**") cho ra <c>IsEmpty = true</c>,
    /// không phải giá trị 0.
    /// </summary>
    public static ResultValue Parse(string? raw)
    {
        var original = raw ?? string.Empty;
        var trimmed = original.Trim();

        if (trimmed.Length == 0)
        {
            return new ResultValue(original, null, null, true);
        }

        var (comparator, body) = ComparatorParsing.Strip(trimmed);

        var titerMatch = TiterPattern.Match(body);
        if (titerMatch.Success)
        {
            if (!decimal.TryParse(titerMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                || !decimal.TryParse(titerMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var denominator))
            {
                return new ResultValue(original, null, null, true); // số quá dài, không vừa decimal
            }

            if (denominator == 0)
            {
                return new ResultValue(original, null, comparator, true);
            }

            return new ResultValue(original, numerator / denominator, comparator, false);
        }

        var normalizedBody = body.Replace(',', '.');
        if (decimal.TryParse(
                normalizedBody,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return new ResultValue(original, number, comparator, false);
        }

        return new ResultValue(original, null, null, true);
    }
}
