using System.Globalization;
using Lis.Core.Results;

namespace Lis.Core.Ingest;

/// <summary>
/// Chuẩn hóa dữ liệu máy gửi trước khi ghép và tính giá trị: barcode bỏ khoảng trắng hai đầu
/// và ký tự điều khiển; giá trị áp hệ số của map kênh và bỏ dấu so sánh nếu map không giữ.
/// </summary>
public static class ReadingNormalizer
{
    /// <summary>
    /// Barcode để so khớp chính xác với <c>sample.barcode</c>: <c>Trim()</c> rồi bỏ mọi ký tự
    /// điều khiển (dưới 0x20 và 0x7F) máy hay gửi kèm (<c>\r</c>, <c>\n</c>, ETX…). Không bao
    /// giờ cắt bớt chữ số — "261103001" vẫn là "261103001", không khớp đuôi "9261103001".
    /// </summary>
    public static string CleanBarcode(string raw)
    {
        var trimmed = raw.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var c in trimmed)
        {
            if (c >= 0x20 && c != 0x7F)
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    /// <summary>
    /// Áp map kênh lên chuỗi giá trị thô: nhân phần số với <paramref name="factor"/> (nếu có và
    /// phần số đọc được), giữ hay bỏ dấu so sánh đứng đầu theo <paramref name="keepComparator"/>.
    /// Chuỗi không phải số ("neg", "***.*") giữ nguyên phần thân.
    /// </summary>
    public static string AdjustValue(string raw, decimal? factor, bool keepComparator)
    {
        var (comparator, body) = ComparatorParsing.Strip(raw.Trim());

        if (factor is not null
            && decimal.TryParse(body.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var number))
        {
            // Chia cho 1.000…0m để bỏ số 0 thừa ở cuối (0.405 × 100 = 40.500 → "40.5").
            var scaled = (number * factor.Value) / 1.0000000000000000000000000000m;
            body = scaled.ToString(CultureInfo.InvariantCulture);
        }

        return keepComparator && comparator is not null ? comparator + body : body;
    }
}
