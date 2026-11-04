using PdfSharp.Fonts;

namespace Lis.Reports.Fonts;

/// <summary>
/// Cấp phông chữ Inter (nhúng trong assembly, giấy phép SIL OFL 1.1) cho PDFsharp. PDFsharp 6
/// không tự tìm được phông hệ thống trên macOS/Linux, mà tem và phiếu in có tiếng Việt có dấu,
/// nên mọi PDF của Lis.Reports dùng chung phông này qua <see cref="FamilyName"/>. Gọi
/// <see cref="Register"/> trước khi tạo <c>XFont</c> — an toàn khi gọi nhiều lần, từ nhiều luồng.
/// </summary>
public sealed class EmbeddedFontResolver : IFontResolver
{
    /// <summary>Tên họ phông dùng khi tạo <c>XFont</c>: <c>new XFont(EmbeddedFontResolver.FamilyName, 9)</c>.</summary>
    public const string FamilyName = "Inter";

    private const string RegularFace = "Inter-Regular";
    private const string BoldFace = "Inter-Bold";

    private static readonly Lock RegisterLock = new();
    private static bool _registered;

    private static readonly Dictionary<string, byte[]> FaceCache = new();

    /// <summary>
    /// Đăng ký resolver với PDFsharp đúng một lần. Mọi tên họ phông đều được đưa về Inter, để
    /// phiếu in trông giống nhau trên máy phát triển (macOS) và máy trạm (Windows), và mã in
    /// phiếu không bao giờ gặp lỗi "không tìm thấy phông" trên máy thiếu phông hệ thống.
    /// </summary>
    public static void Register()
    {
        lock (RegisterLock)
        {
            if (_registered)
            {
                return;
            }

            GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
            _registered = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // Chỉ nhúng mặt thẳng và đậm — PDFsharp mô phỏng nghiêng từ mặt thẳng khi cần.
        return new FontResolverInfo(bold ? BoldFace : RegularFace, mustSimulateBold: false, mustSimulateItalic: italic);
    }

    public byte[]? GetFont(string faceName)
    {
        lock (FaceCache)
        {
            if (FaceCache.TryGetValue(faceName, out var cached))
            {
                return cached;
            }

            var resourceName = $"Lis.Reports.Fonts.{faceName}.ttf";
            using var stream = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Không tìm thấy phông nhúng \"{resourceName}\" trong Lis.Reports.");

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            FaceCache[faceName] = bytes;
            return bytes;
        }
    }
}
