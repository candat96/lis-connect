using System.Globalization;

namespace Lis.Core.Orders;

/// <summary>Ghép tiền tố, ngày và số thứ tự thành một chuỗi barcode (chỉ gồm chữ số).</summary>
public static class BarcodeComposer
{
    /// <summary>
    /// Sinh barcode theo <paramref name="f"/> cho ngày <paramref name="day"/> và số thứ tự
    /// <paramref name="seq"/>. Ném <see cref="InvalidOperationException"/> nếu
    /// <paramref name="seq"/> vượt quá <see cref="BarcodeFormat.MaxSeq"/> (hết số trong ngày).
    /// </summary>
    public static string Compose(BarcodeFormat f, DateOnly day, int seq)
    {
        if (seq > f.MaxSeq)
        {
            throw new InvalidOperationException("Hết số barcode trong ngày — tăng số chữ số thứ tự trong Cài đặt");
        }

        var datePart = day.ToString(f.DatePattern, CultureInfo.InvariantCulture);
        var seqPart = seq.ToString(new string('0', f.SeqDigits), CultureInfo.InvariantCulture);

        return f.Prefix + datePart + seqPart;
    }
}
