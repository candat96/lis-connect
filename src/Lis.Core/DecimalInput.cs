using System.Globalization;

namespace Lis.Core;

/// <summary>
/// Parse một ô nhập số thập phân do người dùng gõ tay (vd khoảng tham chiếu ở màn Danh mục):
/// chấp nhận cả dấu phẩy và dấu chấm làm dấu thập phân (thói quen gõ tiếng Việt lẫn kiểu Anh-Mỹ),
/// nhưng không bao giờ chấp nhận dấu phân cách hàng nghìn — có cả phẩy lẫn chấm trong cùng một
/// ô (vd "1.234,5" hay "1,234.5") bị coi là sai, không được đoán ý thành "1234.5".
/// </summary>
public static class DecimalInput
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var separatorCount = trimmed.Count(c => c is ',' or '.');

        // Có cả hai dấu, hoặc nhiều hơn một dấu cùng loại (vd "1,2,3") — không phải một số thập
        // phân hợp lệ với đúng một dấu phân cách.
        if (separatorCount > 1)
        {
            return false;
        }

        var normalized = trimmed.Replace(',', '.');

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }
}
