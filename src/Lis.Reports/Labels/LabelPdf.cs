using System.Globalization;
using Lis.Reports.Barcode;
using Lis.Reports.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Lis.Reports.Labels;

/// <summary>
/// Dựng PDF tem barcode: mỗi tem một trang, trang đúng bằng kích thước tem (mặc định 50 × 25 mm),
/// gồm họ tên (đậm), giới tính · năm sinh, ngày lấy mẫu, vạch Code 128 vẽ vector và chuỗi số
/// barcode kèm tên loại mẫu bên dưới. Chữ dài thì thu nhỏ rồi cắt bớt kèm "…", không bao giờ
/// tràn ra ngoài tem. Bố cục co giãn theo kích thước tem so với mặc định.
/// </summary>
public static class LabelPdf
{
    private const double BasePaddingMm = 2;
    private const double BaseGapMm = 0.6;
    private const double BaseNameFontPt = 9;
    private const double BaseMetaFontPt = 7;
    private const double BaseDigitsFontPt = 8;
    private const double MinFontPt = 5;
    private const double MinBarcodeHeightMm = 4;

    public static byte[] Render(IReadOnlyList<LabelData> labels, LabelSize size)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(size);

        if (labels.Count == 0)
        {
            throw new ArgumentException("Không có tem nào để in", nameof(labels));
        }

        EmbeddedFontResolver.Register();

        using var document = new PdfDocument();
        document.Info.Title = "Tem barcode";

        foreach (var label in labels)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(size.WidthMm);
            page.Height = XUnit.FromMillimeter(size.HeightMm);

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsUnit.Millimeter);
            DrawLabel(gfx, label, size);
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void DrawLabel(XGraphics gfx, LabelData label, LabelSize size)
    {
        // Tỉ lệ so với tem mặc định 50 × 25 — tem nhỏ hơn thì chữ và lề nhỏ theo, tem lớn hơn thì to lên vừa phải.
        var scale = Math.Clamp(Math.Min(size.WidthMm / LabelSize.Default.WidthMm, size.HeightMm / LabelSize.Default.HeightMm), 0.5, 1.6);
        var padding = BasePaddingMm * scale;
        var gap = BaseGapMm * scale;
        var innerWidth = size.WidthMm - 2 * padding;
        var y = padding;

        // Dòng 1: họ tên (đậm), thu nhỏ/cắt bớt cho vừa chiều rộng tem.
        var (nameFont, nameText) = FitText(gfx, label.PatientName, BaseNameFontPt * scale, bold: true, innerWidth);
        var nameHeight = gfx.MeasureString("Ag", nameFont).Height;
        gfx.DrawString(nameText, nameFont, XBrushes.Black, new XRect(padding, y, innerWidth, nameHeight), XStringFormats.TopLeft);
        y += nameHeight;

        // Dòng 2: giới tính · năm sinh (trái) và ngày lấy mẫu (phải).
        var metaFont = Font(BaseMetaFontPt * scale, bold: false);
        var metaHeight = gfx.MeasureString("Ag", metaFont).Height;
        var dayText = label.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var dayWidth = gfx.MeasureString(dayText, metaFont).Width;
        gfx.DrawString(dayText, metaFont, XBrushes.Black, new XRect(padding, y, innerWidth, metaHeight), XStringFormats.TopRight);

        var (sexYearFont, sexYearText) = FitText(gfx, SexAndYear(label), BaseMetaFontPt * scale, bold: false, innerWidth - dayWidth - gap);
        gfx.DrawString(sexYearText, sexYearFont, XBrushes.Black, new XRect(padding, y, innerWidth, metaHeight), XStringFormats.TopLeft);
        y += metaHeight + gap;

        // Dòng cuối: chuỗi số barcode (trái, đậm) và tên loại mẫu (phải) — tính chiều cao trước để biết vạch cao bao nhiêu.
        var digitsFont = Font(BaseDigitsFontPt * scale, bold: true);
        var digitsHeight = gfx.MeasureString("Ag", digitsFont).Height;
        var digitsWidth = gfx.MeasureString(label.Barcode, digitsFont).Width;
        var bottomLineTop = size.HeightMm - padding - digitsHeight;

        // Vạch Code 128 chiếm phần còn lại ở giữa, không bao giờ thấp hơn mức máy quét đọc được.
        var barcodeTop = y;
        var barcodeHeight = Math.Max(bottomLineTop - gap - barcodeTop, MinBarcodeHeightMm);
        Code128Renderer.Draw(gfx, label.Barcode, new XRect(padding, barcodeTop, innerWidth, barcodeHeight));

        gfx.DrawString(label.Barcode, digitsFont, XBrushes.Black, new XRect(padding, bottomLineTop, innerWidth, digitsHeight), XStringFormats.TopLeft);

        var (specimenFont, specimenText) = FitText(gfx, label.SpecimenName, BaseMetaFontPt * scale, bold: false, innerWidth - digitsWidth - gap);
        gfx.DrawString(specimenText, specimenFont, XBrushes.Black, new XRect(padding, bottomLineTop, innerWidth, digitsHeight), XStringFormats.TopRight);
    }

    /// <summary>
    /// Tạo phông cỡ <paramref name="sizePt"/> điểm. XGraphics của tem dùng đơn vị mm nên cỡ chữ
    /// (và kết quả <c>MeasureString</c>) cũng tính theo mm — phải đổi từ điểm sang mm ở đây.
    /// </summary>
    private static XFont Font(double sizePt, bool bold) =>
        new(EmbeddedFontResolver.FamilyName, XUnit.FromPoint(sizePt).Millimeter, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static string SexAndYear(LabelData label)
    {
        var sex = label.Sex switch
        {
            'M' => "Nam",
            'F' => "Nữ",
            _ => "Khác",
        };

        return label.BirthYear is { } year ? $"{sex} · {year}" : sex;
    }

    /// <summary>
    /// Chọn cỡ chữ và nội dung để <paramref name="text"/> vừa <paramref name="maxWidth"/>: thu
    /// nhỏ dần tới <see cref="MinFontPt"/>, vẫn dài thì cắt bớt ký tự cuối và thêm "…".
    /// </summary>
    private static (XFont Font, string Text) FitText(XGraphics gfx, string text, double fontPt, bool bold, double maxWidth)
    {
        var minPt = Math.Min(MinFontPt, fontPt);
        var font = Font(fontPt, bold);

        while (gfx.MeasureString(text, font).Width > maxWidth && fontPt - 0.5 >= minPt)
        {
            fontPt -= 0.5;
            font = Font(fontPt, bold);
        }

        if (gfx.MeasureString(text, font).Width <= maxWidth)
        {
            return (font, text);
        }

        var core = text;
        while (core.Length > 0)
        {
            core = core[..^1].TrimEnd();
            var candidate = core + "…";
            if (gfx.MeasureString(candidate, font).Width <= maxWidth)
            {
                return (font, candidate);
            }
        }

        return (font, "…");
    }
}
