using Lis.Reports.Barcode;
using Lis.Reports.Labels;
using PDFtoImage;
using Shouldly;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;
using ZXing;
using ZXing.Common;

namespace Lis.Reports.Tests;

/// <summary>
/// Kiểm tra tem barcode: mỗi tem một trang đúng kích thước, chữ trên tem (barcode, họ tên có
/// dấu) đọc lại được bằng PdfPig, và quan trọng nhất — vạch Code 128 (vẽ bằng vector trong PDF)
/// quét lại bằng ZXing ra đúng chuỗi barcode đã cấp, vì tem là thứ nối mẫu với người bệnh.
/// </summary>
public class LabelPdfTests
{
    private const string SampleBarcode = "9261104001";

    private static LabelData SampleLabel(string barcode = SampleBarcode, string specimen = "Máu EDTA") =>
        new(barcode, "Nguyễn Văn Mẫu", 1980, 'M', specimen, new DateOnly(2026, 11, 4));

    [Fact]
    public void Render_OnePagePerLabel()
    {
        var labels = new[]
        {
            SampleLabel("9261104001", "Máu EDTA"),
            SampleLabel("9261104002", "Huyết thanh"),
            SampleLabel("9261104003", "Nước tiểu"),
        };

        var pdf = LabelPdf.Render(labels, LabelSize.Default);

        using var document = PdfDocument.Open(pdf);
        document.NumberOfPages.ShouldBe(3);

        foreach (var page in document.GetPages())
        {
            // 50 × 25 mm = 141.73 × 70.87 pt
            page.Width.ShouldBe(141.73, tolerance: 0.1);
            page.Height.ShouldBe(70.87, tolerance: 0.1);
        }
    }

    [Fact]
    public void Render_ContainsBarcodeText()
    {
        var pdf = LabelPdf.Render([SampleLabel()], LabelSize.Default);

        using var document = PdfDocument.Open(pdf);
        var text = document.GetPage(1).Text;

        text.ShouldContain(SampleBarcode);
        text.ShouldContain("Nguyễn Văn Mẫu");
        text.ShouldContain("1980");
        text.ShouldContain("Máu EDTA");
        text.ShouldContain("04/11/2026");
    }

    [Fact]
    public void Render_LongName_TruncatesWithEllipsis()
    {
        var label = SampleLabel() with { PatientName = "Nguyễn Thị Hoàng Phương Thảo Nguyên Mẫu Rất Dài", SpecimenName = "Huyết thanh (hóa sinh, miễn dịch)" };

        var pdf = LabelPdf.Render([label], new LabelSize(40, 20));

        using var document = PdfDocument.Open(pdf);
        var text = document.GetPage(1).Text;

        text.ShouldContain(SampleBarcode);
        text.ShouldContain("Nguyễn Thị Hoàng");
        text.ShouldNotContain("Rất Dài");
        text.ShouldContain("…");
    }

    [Fact]
    public void Code128_DecodesBack()
    {
        // Hình học vạch lấy từ đúng bước sản xuất (Code128Renderer.Bars) rồi raster hóa ở tỉ lệ 10 px/đơn vị.
        const double widthUnits = 60;
        var bars = Code128Renderer.Bars(SampleBarcode, widthUnits);
        bars.ShouldNotBeEmpty();
        bars.All(b => b.X >= 0 && b.X + b.Width <= widthUnits).ShouldBeTrue();

        var png = RenderBarsToPng(bars, widthUnits, scale: 10, height: 80);

        Decode(SKBitmap.Decode(png)).ShouldBe(SampleBarcode);
    }

    [Fact]
    public void Render_BarcodeInPdfDecodesBack()
    {
        var pdf = LabelPdf.Render([SampleLabel()], LabelSize.Default);

        using var page = RasterizeFirstPage(pdf);

        Decode(page).ShouldBe(SampleBarcode);
    }

    /// <summary>Raster hóa trang đầu bằng PDFtoImage (PDFium) — gói chỉ có bản native cho Windows/Linux/macOS nên phải kiểm tra hệ điều hành trước.</summary>
    private static SKBitmap RasterizeFirstPage(byte[] pdf)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return Conversion.ToImage(pdf, options: new RenderOptions(Dpi: 300));
        }

        throw new PlatformNotSupportedException("PDFium chỉ có bản cho Windows, Linux và macOS");
    }

    [Fact]
    public void LabelSize_FromSetting_ParsesOrFallsBackToDefault()
    {
        LabelSize.FromSetting("50x25").ShouldBe(new LabelSize(50, 25));
        LabelSize.FromSetting("40,5x20").ShouldBe(new LabelSize(40.5, 20));
        LabelSize.FromSetting(null).ShouldBe(LabelSize.Default);
        LabelSize.FromSetting("").ShouldBe(LabelSize.Default);
        LabelSize.FromSetting("abc").ShouldBe(LabelSize.Default);
        LabelSize.FromSetting("0x25").ShouldBe(LabelSize.Default);
        LabelSize.FromSetting("50").ShouldBe(LabelSize.Default);

        new LabelSize(50, 25).ToSetting().ShouldBe("50x25");
        new LabelSize(40.5, 20).ToSetting().ShouldBe("40.5x20");
    }

    private static byte[] RenderBarsToPng(IReadOnlyList<Code128Bar> bars, double widthUnits, double scale, int height)
    {
        var width = (int)Math.Ceiling(widthUnits * scale);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        foreach (var bar in bars)
        {
            canvas.DrawRect((float)(bar.X * scale), 0, (float)(bar.Width * scale), height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string? Decode(SKBitmap bitmap)
    {
        using var gray = bitmap.Copy(SKColorType.Gray8);
        var source = new RGBLuminanceSource(gray.Bytes, gray.Width, gray.Height, RGBLuminanceSource.BitmapFormat.Gray8);

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.CODE_128], TryHarder = true },
        };

        return reader.Decode(source)?.Text;
    }
}
