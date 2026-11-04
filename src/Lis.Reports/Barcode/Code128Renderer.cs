using PdfSharp.Drawing;
using ZXing.OneD;

namespace Lis.Reports.Barcode;

/// <summary>Một vạch đen của Code 128: vị trí và bề rộng tính từ mép trái vùng vẽ, cùng đơn vị với vùng vẽ.</summary>
public sealed record Code128Bar(double X, double Width);

/// <summary>
/// Mã hóa chuỗi thành Code 128 (qua ZXing) và vẽ thành vạch vector lên PDF — không chèn ảnh
/// raster, để máy in tem nhiệt in vạch sắc nét ở mọi độ phân giải và máy quét đọc lại đúng.
/// </summary>
public static class Code128Renderer
{
    /// <summary>
    /// Vùng trắng hai bên (quiet zone) tính theo số module — Code 128 yêu cầu tối thiểu 10 module
    /// mỗi bên, không có thì máy quét bỏ qua vạch đầu/cuối.
    /// </summary>
    public const int QuietZoneModules = 10;

    /// <summary>
    /// Dãy module của Code 128 cho <paramref name="content"/>: <c>true</c> là vạch đen, <c>false</c>
    /// là khoảng trắng, mỗi phần tử rộng đúng một module, chưa gồm quiet zone.
    /// </summary>
    public static bool[] Modules(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            throw new ArgumentException("Nội dung barcode không được rỗng", nameof(content));
        }

        return new Code128Writer().encode(content);
    }

    /// <summary>
    /// Hình học các vạch đen khi dàn Code 128 vào vùng rộng <paramref name="width"/>: quiet zone
    /// nằm trong vùng, các module đen kề nhau gộp thành một vạch để không có khe hở khi raster
    /// hóa. Đây là bước thuần túy (không vẽ) để kiểm thử được; <see cref="Draw"/> chỉ tô các vạch này.
    /// </summary>
    public static IReadOnlyList<Code128Bar> Bars(string content, double width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Bề rộng vùng vẽ barcode phải lớn hơn 0");
        }

        var modules = Modules(content);
        var totalModules = modules.Length + QuietZoneModules * 2;
        var moduleWidth = width / totalModules;
        var left = QuietZoneModules * moduleWidth;

        var bars = new List<Code128Bar>();
        var i = 0;
        while (i < modules.Length)
        {
            if (!modules[i])
            {
                i++;
                continue;
            }

            var runStart = i;
            while (i < modules.Length && modules[i])
            {
                i++;
            }

            bars.Add(new Code128Bar(left + runStart * moduleWidth, (i - runStart) * moduleWidth));
        }

        return bars;
    }

    /// <summary>
    /// Vẽ Code 128 vào <paramref name="area"/> (đơn vị theo <paramref name="gfx"/>): vạch cao đúng
    /// bằng <paramref name="area"/>, bề ngang theo <see cref="Bars"/>.
    /// </summary>
    public static void Draw(XGraphics gfx, string content, XRect area)
    {
        foreach (var bar in Bars(content, area.Width))
        {
            gfx.DrawRectangle(XBrushes.Black, area.X + bar.X, area.Y, bar.Width, area.Height);
        }
    }
}
