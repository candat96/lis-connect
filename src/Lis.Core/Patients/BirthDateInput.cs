using System.Globalization;
using System.Text.RegularExpressions;

namespace Lis.Core.Patients;

/// <summary>Ngày sinh đọc từ một ô nhập: đủ ngày (<see cref="Dob"/>) hoặc chỉ năm (<see cref="BirthYear"/>).</summary>
public sealed record BirthDate(DateOnly? Dob, int? BirthYear)
{
    public int Year => Dob?.Year ?? BirthYear!.Value;
}

/// <summary>
/// Một ô "Ngày sinh" duy nhất ở màn Tiếp nhận: nhận <c>dd/MM/yyyy</c> (cả <c>d/M/yyyy</c>, dấu
/// <c>-</c> hoặc <c>.</c>), 8 chữ số liền <c>ddMMyyyy</c>, hoặc chỉ năm <c>yyyy</c> khi người bệnh
/// không nhớ ngày tháng.
/// </summary>
public static partial class BirthDateInput
{
    [GeneratedRegex(@"^(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{4})$")]
    private static partial Regex SeparatedDate();

    /// <summary>Gõ đủ 8 chữ số liền (vd "01011990") → "01/01/1990" để người dùng thấy ngay ngày đã hiểu; còn lại giữ nguyên.</summary>
    public static string Format(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 8 && trimmed.All(char.IsAsciiDigit)
            ? $"{trimmed[..2]}/{trimmed[2..4]}/{trimmed[4..]}"
            : text;
    }

    /// <summary>Đọc ô ngày sinh. Không hợp lệ (sai dạng, ngày không có thật, sau hôm nay, trước 1900) → false kèm lý do.</summary>
    public static bool TryParse(string? text, DateOnly today, out BirthDate? value, out string? error)
    {
        value = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            error = "Chưa nhập ngày sinh (dd/MM/yyyy hoặc chỉ năm yyyy)";
            return false;
        }

        if (trimmed.Length == 4 && trimmed.All(char.IsAsciiDigit))
        {
            var year = int.Parse(trimmed, CultureInfo.InvariantCulture);
            if (year < 1900 || year > today.Year)
            {
                error = $"Năm sinh phải từ 1900 đến {today.Year}";
                return false;
            }

            value = new BirthDate(null, year);
            error = null;
            return true;
        }

        var formatted = Format(trimmed);
        var match = SeparatedDate().Match(formatted);
        if (!match.Success)
        {
            error = "Ngày sinh phải dạng dd/MM/yyyy (vd 15/06/1980) hoặc chỉ năm (vd 1980)";
            return false;
        }

        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var yearPart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || yearPart < 1900 || day < 1 || day > DateTime.DaysInMonth(yearPart, month))
        {
            error = $"Ngày {formatted} không có thật";
            return false;
        }

        var dob = new DateOnly(yearPart, month, day);
        if (dob > today)
        {
            error = "Ngày sinh sau hôm nay";
            return false;
        }

        value = new BirthDate(dob, null);
        error = null;
        return true;
    }

    /// <summary>Gợi ý hiển thị cạnh ô: "15/06/1980 · 46 tuổi", "Năm 1980 · 46 tuổi", hoặc lý do sai.</summary>
    public static string Describe(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (!TryParse(text, today, out var value, out var error))
        {
            return error!;
        }

        var age = value!.Dob is { } dob
            ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0)
            : today.Year - value.BirthYear!.Value;
        var when = value.Dob is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : $"Năm {value.BirthYear}";
        return age >= 1 ? $"{when} · {age} tuổi" : $"{when} · dưới 1 tuổi";
    }
}
