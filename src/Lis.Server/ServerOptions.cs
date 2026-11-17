namespace Lis.Server;

/// <summary>
/// Cấu hình chỉ cần khi chạy server web (lệnh quản trị dòng lệnh không đọc). <see cref="LocalUtcOffsetMinutes"/>:
/// múi giờ phòng khám để chia "ngày" (mặc định 420 = UTC+7). <see cref="SetupKey"/>: khoá khởi tạo phòng khám mới (<c>Setup:Key</c> hoặc biến môi trường <c>SETUP_KEY</c>; trống → server tự sinh).
/// <see cref="WebRoot"/>: thư mục chứa giao diện web đã build.
/// <see cref="PublicUrl"/>: địa chỉ công khai của server (<c>PublicUrl</c> hoặc biến môi trường <c>PUBLIC_URL</c>) đưa vào mã ghép nối agent; trống → lấy theo request.
/// <see cref="AgentDistDir"/>: thư mục chứa bản cài agent đã build (<c>Agent:DistDir</c> hoặc <c>AGENT_DIST_DIR</c>).
/// </summary>
public sealed record ServerOptions(
    string JwtSecret, int LocalUtcOffsetMinutes, string? SetupKey, string? WebRoot, string? PublicUrl, string? AgentDistDir)
{
    public const string PlaceholderSecret = "CHANGE_ME";
    public const int DefaultLocalUtcOffsetMinutes = 420;
    public const int MinSecretLength = 24;
    public const int MinSetupKeyLength = 16;

    /// <summary>Đọc cấu hình; <c>Jwt:Secret</c> thiếu, còn là "CHANGE_ME" hoặc ngắn hơn 24 ký tự → <see cref="InvalidOperationException"/>.</summary>
    public static ServerOptions From(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || secret == PlaceholderSecret || secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException(
                $"Cấu hình Jwt:Secret chưa đặt (hoặc còn là {PlaceholderSecret}, hoặc ngắn hơn {MinSecretLength} ký tự) — đặt một chuỗi bí mật ngẫu nhiên dài trước khi chạy server web");
        }

        return new ServerOptions(
            secret,
            LocalUtcOffsetFrom(configuration),
            SetupKeyOrNull(configuration["Setup:Key"]) ?? SetupKeyOrNull(configuration["SETUP_KEY"]),
            string.IsNullOrWhiteSpace(configuration["Web:Root"]) ? null : configuration["Web:Root"],
            PublicUrlOrNull(NonBlank(configuration["PublicUrl"]) ?? NonBlank(configuration["PUBLIC_URL"])),
            NonBlank(configuration["Agent:DistDir"]) ?? NonBlank(configuration["AGENT_DIST_DIR"]));
    }

    /// <summary>Địa chỉ công khai phải là URL tuyệt đối http/https, không có query hay fragment; đặt mà sai thì không khởi động (mã ghép nối sẽ hỏng).</summary>
    private static string? PublicUrlOrNull(string? value)
    {
        if (value is null) return null;
        var url = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Contains('?') || url.Contains('#'))
        {
            throw new InvalidOperationException(
                "PublicUrl (PUBLIC_URL) phải là địa chỉ tuyệt đối http hoặc https, không có ?query hay #fragment — ví dụ https://lis.thu.vn");
        }

        return url;
    }

    // "CHANGE_ME" (giá trị mẫu trong .env.example) và chuỗi trống coi như chưa đặt (server tự sinh mã); đã đặt thì phải đủ dài.
    private static string? SetupKeyOrNull(string? value)
    {
        var key = value == Api.SetupGuard.Placeholder ? null : NonBlank(value);
        if (key is not null && key.Length < MinSetupKeyLength)
        {
            throw new InvalidOperationException(
                $"Mã thiết lập (Setup:Key / SETUP_KEY) ngắn hơn {MinSetupKeyLength} ký tự — đặt mã dài hơn, hoặc để trống để server tự sinh mã mỗi lần chạy");
        }

        return key;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Múi giờ phòng khám (phút lệch so với UTC, <c>LocalUtcOffsetMinutes</c>); không đặt hoặc sai → 420 (UTC+7).</summary>
    public static int LocalUtcOffsetFrom(IConfiguration configuration) =>
        int.TryParse(configuration["LocalUtcOffsetMinutes"], out var minutes) ? minutes : DefaultLocalUtcOffsetMinutes;
}
