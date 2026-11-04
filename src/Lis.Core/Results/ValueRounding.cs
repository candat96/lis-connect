using System.Globalization;

namespace Lis.Core.Results;

/// <summary>
/// Làm tròn chuỗi giá trị kết quả cho mục đích hiển thị/in ấn, dùng số học
/// <see cref="decimal"/> thuần (không dùng <see cref="double"/>) để tránh sai số.
/// </summary>
public static class ValueRounding
{
    private static readonly string[] Comparators = { "<=", ">=", "<", ">" };

    /// <summary>
    /// Làm tròn kiểu "half away from zero" và giữ đúng số chữ số thập phân
    /// (kể cả số 0 ở cuối, ví dụ "4.8" với 2 chữ số thành "4.80"). Giữ nguyên
    /// dấu so sánh đứng đầu nếu có. Chuỗi không phải số (ví dụ "Âm tính") được
    /// trả về nguyên văn, không bị đụng tới.
    /// </summary>
    public static string Round(string raw, int? decimals)
    {
        if (decimals is null)
        {
            return raw;
        }

        var trimmed = raw.Trim();

        var comparator = string.Empty;
        var body = trimmed;
        foreach (var candidate in Comparators)
        {
            if (body.StartsWith(candidate, StringComparison.Ordinal))
            {
                comparator = candidate;
                body = body[candidate.Length..].Trim();
                break;
            }
        }

        var normalizedBody = body.Replace(',', '.');
        if (!decimal.TryParse(
                normalizedBody,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return raw;
        }

        var rounded = Math.Round(number, decimals.Value, MidpointRounding.AwayFromZero);
        var numberText = rounded.ToString("F" + decimals.Value, CultureInfo.InvariantCulture);

        return comparator + numberText;
    }
}
