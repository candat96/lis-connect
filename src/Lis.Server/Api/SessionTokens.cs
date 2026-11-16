using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lis.Server.Api;

/// <summary>
/// Token phiên đăng nhập: JWT HS256, hết hạn sau 12 giờ, mang <c>sub</c> (tài khoản) và <c>clinicId</c> (phòng khám) —
/// token xác định luôn phòng khám mà request được phép chạm.
/// </summary>
public sealed class SessionTokens(string secret, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
    private const string HeaderJson = """{"alg":"HS256","typ":"JWT"}""";
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public string Issue(Guid userId, Guid clinicId)
    {
        var now = clock.GetUtcNow();
        var payload = JsonSerializer.Serialize(new
        {
            sub = userId,
            clinicId,
            iat = now.ToUnixTimeSeconds(),
            exp = (now + Lifetime).ToUnixTimeSeconds(),
        });
        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(HeaderJson))}.{Encode(Encoding.UTF8.GetBytes(payload))}";
        return $"{signingInput}.{Encode(Sign(signingInput))}";
    }

    /// <summary>
    /// Đọc token từ giá trị header <c>Authorization</c> (<c>Bearer &lt;token&gt;</c>; chấp nhận cả token trần).
    /// Hỏng, sai chữ ký, sai thuật toán hoặc hết hạn → null.
    /// </summary>
    public (Guid UserId, Guid ClinicId)? Read(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return null;
        }

        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..].Trim() : bearer.Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Sign($"{parts[0]}.{parts[1]}"), Decode(parts[2])))
            {
                return null;
            }

            using var header = JsonDocument.Parse(Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "HS256")
            {
                return null;
            }

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var root = payload.RootElement;
            if (root.GetProperty("exp").GetInt64() <= clock.GetUtcNow().ToUnixTimeSeconds())
            {
                return null;
            }

            return (Guid.Parse(root.GetProperty("sub").GetString()!), Guid.Parse(root.GetProperty("clinicId").GetString()!));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private byte[] Sign(string signingInput) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(signingInput));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
