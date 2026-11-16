namespace Lis.Server;

/// <summary>
/// Cấu hình chỉ cần khi chạy server web (lệnh quản trị dòng lệnh không đọc). <see cref="LocalUtcOffsetMinutes"/>:
/// múi giờ phòng khám để chia "ngày" (mặc định 420 = UTC+7). <see cref="SetupKey"/>: khoá khởi tạo phòng khám mới.
/// <see cref="WebRoot"/>: thư mục chứa giao diện web đã build.
/// </summary>
public sealed record ServerOptions(string JwtSecret, int LocalUtcOffsetMinutes, string? SetupKey, string? WebRoot)
{
    public const string PlaceholderSecret = "CHANGE_ME";
    public const int MinSecretLength = 24;

    /// <summary>Đọc cấu hình; <c>Jwt:Secret</c> thiếu, còn là "CHANGE_ME" hoặc ngắn hơn 24 ký tự → <see cref="InvalidOperationException"/>.</summary>
    public static ServerOptions From(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || secret == PlaceholderSecret || secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException(
                $"Cấu hình Jwt:Secret chưa đặt (hoặc còn là {PlaceholderSecret}, hoặc ngắn hơn {MinSecretLength} ký tự) — đặt một chuỗi bí mật ngẫu nhiên dài trước khi chạy server web");
        }

        var offset = int.TryParse(configuration["LocalUtcOffsetMinutes"], out var minutes) ? minutes : 420;
        return new ServerOptions(
            secret,
            offset,
            string.IsNullOrWhiteSpace(configuration["Setup:Key"]) ? null : configuration["Setup:Key"],
            string.IsNullOrWhiteSpace(configuration["Web:Root"]) ? null : configuration["Web:Root"]);
    }
}
