using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lis.Core.Agents;

/// <summary>
/// Mã ghép nối agent: gói địa chỉ server, mã phòng khám và bí mật dùng một lần thành một chuỗi
/// <c>LIS1.&lt;base64url&gt;</c> — nằm gọn trong tên tệp cài (<c>LisAgent_LIS1.xxx.exe</c>) hoặc dán được vào ô nhập.
/// </summary>
public sealed partial class PairingCode
{
    public const string Prefix = "LIS1.";

    /// <summary>Bí mật = 24 byte ngẫu nhiên → đúng 32 ký tự base64url, không có padding.</summary>
    private const int SecretLength = 32;

    public PairingCode(string serverUrl, string clinicCode, string secret)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(clinicCode);
        ArgumentNullException.ThrowIfNull(secret);

        var url = serverUrl.Trim().TrimEnd('/');
        if (url.Contains('\n') || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || url.Contains('?') || url.Contains('#'))
        {
            throw new ArgumentException("Địa chỉ server phải là URL tuyệt đối http/https, không có query hay fragment", nameof(serverUrl));
        }

        var code = clinicCode.Trim();
        if (code.Length == 0 || code.Contains('\n'))
        {
            throw new ArgumentException("Thiếu mã phòng khám", nameof(clinicCode));
        }

        if (!SecretPattern().IsMatch(secret))
        {
            throw new ArgumentException($"Bí mật ghép nối phải đúng {SecretLength} ký tự base64url", nameof(secret));
        }

        ServerUrl = url;
        ClinicCode = code;
        Secret = secret;
    }

    public string ServerUrl { get; }

    public string ClinicCode { get; }

    public string Secret { get; }

    /// <summary>Bí mật mới: 24 byte ngẫu nhiên mã hoá base64url (32 ký tự).</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    public string Encode() =>
        Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{ServerUrl}\n{ClinicCode}\n{Secret}"));

    /// <summary>Tìm mã ở bất kỳ đâu trong chuỗi (tên tệp, văn bản dán có khoảng trắng / xuống dòng). Không có hoặc hỏng → false.</summary>
    public static bool TryFind(string? text, [NotNullWhen(true)] out PairingCode? code)
    {
        code = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = CodePattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(match.ValueSpan[Prefix.Length..]);
            var parts = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
            if (parts.Length != 3)
            {
                return false;
            }

            code = new PairingCode(parts[0], parts[1], parts[2]);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"LIS1\.[A-Za-z0-9_-]+")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{32}$")]
    private static partial Regex SecretPattern();
}
