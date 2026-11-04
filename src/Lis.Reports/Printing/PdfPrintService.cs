using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using PDFtoImage;
using SkiaSharp;

namespace Lis.Reports.Printing;

/// <summary>
/// In PDF ra máy in. Trên Windows: raster hóa từng trang bằng PDFtoImage (PDFium) đúng độ phân
/// giải của máy in rồi in qua <see cref="PrintDocument"/> (System.Drawing.Printing), khổ giấy đặt
/// đúng bằng trang PDF để máy in tem nhận đúng cỡ tem và ảnh không bị co giãn lần nữa. Trên hệ
/// khác (máy phát triển macOS/Linux): lưu PDF tạm và mở bằng ứng dụng mặc định để xem — không in thật.
/// </summary>
public sealed class PdfPrintService : IPrintService
{
    private const int FallbackDpi = 300;

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

        var dpi = PrinterDpi(document.PrinterSettings.DefaultPageSettings.PrinterResolution);
        var pages = RasterizePages(pdf, dpi, ct);
        try
        {
            var pageIndex = 0;

            document.QueryPageSettings += (_, e) =>
            {
                var page = pages[pageIndex];
                e.PageSettings!.PaperSize = new PaperSize("LIS", page.WidthHundredthsInch, page.HeightHundredthsInch);
                e.PageSettings.Margins = new Margins(0, 0, 0, 0);
            };

            document.PrintPage += (_, e) =>
            {
                if (ct.IsCancellationRequested)
                {
                    e.Cancel = true;
                    return;
                }

                var page = pages[pageIndex];
                var graphics = e.Graphics!;
                graphics.PageUnit = GraphicsUnit.Display;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(page.Image, new Rectangle(0, 0, page.WidthHundredthsInch, page.HeightHundredthsInch));

                pageIndex++;
                e.HasMorePages = pageIndex < pages.Count;
            };

            // Lề đã đặt 0 ở QueryPageSettings; OriginAtMargins = true để gốc vẽ là góc vật lý của
            // tờ giấy (GDI tự dịch đi phần lề cứng), không phải góc vùng in được — máy in có lề
            // cứng khác 0 sẽ không làm tem bị xê dịch/cắt mép.
            document.OriginAtMargins = true;

            ct.ThrowIfCancellationRequested();
            document.Print();
            // Hủy giữa chừng: PrintPage chỉ đặt e.Cancel, Print() vẫn trả về bình thường — báo
            // hủy ra ngoài để PrintAsync không kết thúc "thành công" trong im lặng.
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            DisposeAll(pages);
        }
    }

    /// <summary>
    /// Độ phân giải thật của máy in (dpi). Khi driver chỉ báo mức "Draft/Low/Medium/High"
    /// (X/Y ≤ 0) thì dùng <see cref="FallbackDpi"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int PrinterDpi(PrinterResolution resolution) =>
        resolution.X > 0 && resolution.Y > 0 ? Math.Min(resolution.X, resolution.Y) : FallbackDpi;

    /// <summary>
    /// Raster hóa từng trang PDF ở <paramref name="dpi"/> — kích thước trang tính theo 1/100 inch
    /// (đơn vị của System.Drawing.Printing). Ảnh GDI+ được dựng đầy đủ ngay (không giữ stream),
    /// vì <c>Image.FromStream</c> giải mã lười và sẽ lỗi khi stream đã đóng. Lỗi giữa chừng thì
    /// giải phóng các trang đã dựng.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<RasterPage> RasterizePages(byte[] pdf, int dpi, CancellationToken ct)
    {
        var pages = new List<RasterPage>();
        try
        {
            var pageCount = Conversion.GetPageCount(pdf);
            // Chỉ khử răng cưa chữ — vạch barcode (đường/hình) giữ cạnh sắc để máy quét đọc đúng.
            var options = new RenderOptions(Dpi: dpi, AntiAliasing: PdfAntiAliasing.Text);

            for (var i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                using var bitmap = Conversion.ToImage(pdf, page: i, options: options);
                pages.Add(new RasterPage(
                    ToGdiBitmap(bitmap),
                    (int)Math.Round(bitmap.Width * 100.0 / dpi),
                    (int)Math.Round(bitmap.Height * 100.0 / dpi)));
            }

            return pages;
        }
        catch
        {
            DisposeAll(pages);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap ToGdiBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        using var decoded = new Bitmap(stream);

        // Sao chép sang Bitmap mới để ảnh không còn phụ thuộc vào stream đã đóng.
        return new Bitmap(decoded);
    }

    [SupportedOSPlatform("windows")]
    private static void DisposeAll(List<RasterPage> pages)
    {
        foreach (var page in pages)
        {
            page.Image.Dispose();
        }

        pages.Clear();
    }

    private static void OpenWithDefaultApp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lis-connect-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private sealed record RasterPage(Image Image, int WidthHundredthsInch, int HeightHundredthsInch);
}
