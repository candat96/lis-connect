using PdfSharp.Drawing;
using ZXing.OneD;

namespace Lis.Reports.Barcode;

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
    /// Vẽ Code 128 vào <paramref name="area"/> (đơn vị theo <paramref name="gfx"/>): vạch căn giữa
    /// theo chiều ngang, cao đúng bằng <paramref name="area"/>, quiet zone nằm trong vùng này.
    /// Các module đen kề nhau gộp thành một hình chữ nhật để không có khe hở khi raster hóa.
    /// </summary>
    public static void Draw(XGraphics gfx, string content, XRect area)
    {
        var modules = Modules(content);
        var totalModules = modules.Length + QuietZoneModules * 2;
        var moduleWidth = area.Width / totalModules;
        var left = area.X + QuietZoneModules * moduleWidth;

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

            var x = left + runStart * moduleWidth;
            var width = (i - runStart) * moduleWidth;
            gfx.DrawRectangle(XBrushes.Black, x, area.Y, width, area.Height);
        }
    }
}
