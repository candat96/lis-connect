namespace Lis.Core.Orders;

/// <summary>
/// Quy tắc sinh barcode của một cơ sở (bảng <c>facility</c>): tiền tố, mẫu ngày (vd
/// <c>yyMMdd</c>), số chữ số thứ tự trong ngày, và chế độ gom mẫu (<c>per-specimen</c> hoặc
/// <c>single</c> — xem <see cref="SampleGrouper"/>). Dùng chung cho cả barcode mẫu bệnh phẩm
/// (tiền tố cấu hình, vd "9") và mã người bệnh (tiền tố cố định "BN").
/// </summary>
public sealed record BarcodeFormat(string Prefix, string DatePattern, int SeqDigits, string Mode)
{
    /// <summary>
    /// Tổng độ dài barcode sinh ra: tiền tố + phần ngày (giả định mỗi ký tự của
    /// <see cref="DatePattern"/> tương ứng một chữ số in ra, đúng với các mẫu chỉ gồm chữ số
    /// như <c>yyMMdd</c>) + số chữ số thứ tự.
    /// </summary>
    public int Length => Prefix.Length + DatePattern.Length + SeqDigits;

    /// <summary>Số thứ tự lớn nhất có thể biểu diễn với <see cref="SeqDigits"/> chữ số.</summary>
    public int MaxSeq => (int)Math.Pow(10, SeqDigits) - 1;
}
