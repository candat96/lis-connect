using System.Security.Cryptography;

namespace Lis.Data.Local;

/// <summary>
/// Mã hóa bí mật bằng AES-GCM với khóa ngẫu nhiên lưu trong tệp <c>dev.key</c> cùng thư mục
/// cấu hình. Dùng cho các hệ điều hành không có DPAPI (macOS, Linux) khi phát triển/kiểm thử.
/// </summary>
public sealed class DevKeySecretProtector : ISecretProtector
{
    private const int KeySizeBytes = 32; // AES-256
    private const int NonceSizeBytes = 12; // Kích thước nonce chuẩn của AES-GCM
    private const int TagSizeBytes = 16;

    private readonly string _keyFile;

    public DevKeySecretProtector(string directory)
    {
        Directory.CreateDirectory(directory);
        _keyFile = Path.Combine(directory, "dev.key");
    }

    public byte[] Protect(byte[] plain)
    {
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var cipherText = new byte[plain.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plain, cipherText, tag);

        // Định dạng lưu: nonce (12) + tag (16) + dữ liệu mã hóa.
        var result = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, result, nonce.Length + tag.Length, cipherText.Length);
        return result;
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (cipher.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Dữ liệu mã hóa không hợp lệ.");
        }

        var key = LoadOrCreateKey();
        var nonce = cipher.AsSpan(0, NonceSizeBytes).ToArray();
        var tag = cipher.AsSpan(NonceSizeBytes, TagSizeBytes).ToArray();
        var cipherText = cipher.AsSpan(NonceSizeBytes + TagSizeBytes).ToArray();
        var plain = new byte[cipherText.Length];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, cipherText, tag, plain);
        return plain;
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyFile))
        {
            return Convert.FromBase64String(File.ReadAllText(_keyFile));
        }

        var newKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
        File.WriteAllText(_keyFile, Convert.ToBase64String(newKey));
        return newKey;
    }
}
