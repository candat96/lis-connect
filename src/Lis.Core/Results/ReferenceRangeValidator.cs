namespace Lis.Core.Results;

/// <summary>
/// Kiểm tra tính hợp lệ của một danh sách khoảng tham chiếu trước khi lưu: thấp ≤ cao,
/// nguy hiểm thấp ≤ thấp (khi cả hai có), cao ≤ nguy hiểm cao (khi cả hai có), tuổi từ ≤ tuổi
/// đến. Không kiểm tra việc nhập liệu (vd giá trị không phải số) — việc đó xảy ra trước khi có
/// được <see cref="ReferenceRange"/> (xem nơi gọi ở <c>Lis.App</c>).
/// </summary>
public static class ReferenceRangeValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<ReferenceRange> ranges)
    {
        var errors = new List<string>();

        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            var rowLabel = $"Dòng {i + 1}";

            if (range.Low is { } low && range.High is { } high && low > high)
            {
                errors.Add($"{rowLabel}: giá trị thấp phải nhỏ hơn hoặc bằng giá trị cao");
            }

            if (range.CriticalLow is { } criticalLow && range.Low is { } lowAgainstCritical && criticalLow > lowAgainstCritical)
            {
                errors.Add($"{rowLabel}: nguy hiểm thấp phải nhỏ hơn hoặc bằng giá trị thấp");
            }

            if (range.High is { } highAgainstCritical && range.CriticalHigh is { } criticalHigh && highAgainstCritical > criticalHigh)
            {
                errors.Add($"{rowLabel}: giá trị cao phải nhỏ hơn hoặc bằng nguy hiểm cao");
            }

            if (range.AgeFromDays is { } ageFromDays && range.AgeToDays is { } ageToDays && ageFromDays > ageToDays)
            {
                errors.Add($"{rowLabel}: tuổi từ phải nhỏ hơn hoặc bằng tuổi đến");
            }
        }

        return errors;
    }
}
