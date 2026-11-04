namespace Lis.Core.Results;

/// <summary>
/// Quy đổi giá trị định tính thô sang chuỗi hiển thị và cờ ghi đè theo các
/// luật đã cấu hình. So <see cref="QualitativeRule.RawValue"/> không phân biệt
/// hoa thường, bỏ khoảng trắng hai đầu. Luật đúng máy ưu tiên hơn luật máy null.
/// Không có luật khớp thì trả nguyên giá trị thô, không có cờ ghi đè.
/// </summary>
public static class QualitativeMapper
{
    public static (string Display, ResultFlag? FlagOverride) Apply(
        string raw,
        Guid serviceId,
        Guid? instrumentId,
        IEnumerable<QualitativeRule> rules)
    {
        var normalizedRaw = raw.Trim();

        var matchedRule = rules
            .Where(rule => rule.ServiceId == serviceId
                && string.Equals(rule.RawValue.Trim(), normalizedRaw, StringComparison.OrdinalIgnoreCase)
                && (rule.InstrumentId is null || rule.InstrumentId == instrumentId))
            .OrderBy(rule => rule.InstrumentId is not null && rule.InstrumentId == instrumentId ? 0 : 1)
            .FirstOrDefault();

        if (matchedRule is null)
        {
            return (raw, null);
        }

        return (matchedRule.DisplayValue, matchedRule.Flag);
    }
}
