using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using PDFtoImage;
using SkiaSharp;

namespace Lis.Reports.Printing;

/// <summary>
/// In PDF ra máy in. Trên Windows: raster hóa từng trang bằng PDFtoImage (PDFium) rồi in qua
/// <see cref="PrintDocument"/> (System.Drawing.Printing), khổ giấy đặt đúng bằng trang PDF để
/// máy in tem nhận đúng cỡ tem. Trên hệ khác (máy phát triển macOS/Linux): lưu PDF tạm và mở
/// bằng ứng dụng mặc định để xem — không in thật.
/// </summary>
public sealed class PdfPrintService : IPrintService
{
    private const int PrintDpi = 300;

    public IReadOnlyList<string> Printers() => OperatingSystem.IsWindows() ? WindowsPrinters() : [];

    public Task PrintAsync(byte[] pdf, string? printerName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        if (OperatingSystem.IsWindows())
        {
            return PrintOnWindowsAsync(pdf, printerName, ct);
        }

        OpenWithDefaultApp(pdf);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static Task PrintOnWindowsAsync(byte[] pdf, string? printerName, CancellationToken ct) =>
        Task.Run(() => PrintOnWindows(pdf, printerName, ct), ct);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> WindowsPrinters()
    {
        var names = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            names.Add(name);
        }

        return names;
    }

    [SupportedOSPlatform("windows")]
    private static void PrintOnWindows(byte[] pdf, string? printerName, CancellationToken ct)
    {
        var pages = RasterizePages(pdf, ct);
        try
        {
            using var document = new PrintDocument();
            document.PrintController = new StandardPrintController();
            document.DocumentName = "LIS Connect";

            if (!string.IsNullOrWhiteSpace(printerName))
            {
                document.PrinterSettings.PrinterName = printerName;
            }

            if (!document.PrinterSettings.IsValid)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(printerName)
                        ? "Máy này chưa có máy in mặc định"
                        : $"Không tìm thấy máy in \"{printerName}\"");
            }

            var pageIndex = 0;
            document.PrintPage += (_, e) =>
            {
                ct.ThrowIfCancellationRequested();

                var page = pages[pageIndex];
                e.Graphics!.DrawImage(page.Image, 0, 0, page.WidthHundredthsInch, page.HeightHundredthsInch);

                pageIndex++;
                e.HasMorePages = pageIndex < pages.Count;
            };

            document.QueryPageSettings += (_, e) =>
            {
                var page = pages[pageIndex];
                e.PageSettings!.PaperSize = new PaperSize("LIS", page.WidthHundredthsInch, page.HeightHundredthsInch);
                e.PageSettings.Margins = new Margins(0, 0, 0, 0);
            };

            document.OriginAtMargins = false;
            document.Print();
        }
        finally
        {
            foreach (var page in pages)
            {
                page.Image.Dispose();
            }
        }
    }

    /// <summary>Raster hóa từng trang PDF ở <see cref="PrintDpi"/> — kích thước trang tính theo 1/100 inch (đơn vị của System.Drawing.Printing).</summary>
    [SupportedOSPlatform("windows")]
    private static List<RasterPage> RasterizePages(byte[] pdf, CancellationToken ct)
    {
        var pages = new List<RasterPage>();
        var pageCount = Conversion.GetPageCount(pdf);
        var options = new RenderOptions(Dpi: PrintDpi, AntiAliasing: PdfAntiAliasing.All);

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var bitmap = Conversion.ToImage(pdf, page: i, options: options);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());

            var drawingImage = Image.FromStream(stream);
            pages.Add(new RasterPage(
                drawingImage,
                (int)Math.Round(bitmap.Width * 100.0 / PrintDpi),
                (int)Math.Round(bitmap.Height * 100.0 / PrintDpi)));
        }

        return pages;
    }

    private static void OpenWithDefaultApp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lis-connect-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private sealed record RasterPage(Image Image, int WidthHundredthsInch, int HeightHundredthsInch);
}
