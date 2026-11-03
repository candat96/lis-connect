using System.Security.Cryptography;

namespace Lis.Data.Local;

/// <summary>
/// Mã hóa bí mật bằng AES-GCM với khóa ngẫu nhiên lưu trong tệp <c>dev.key</c> cùng thư mục
/// cấu hình. Dùng cho các hệ điều hành không có DPAPI (macOS, Linux) khi phát triển/kiểm thử.
/// </summary>
public sealed class DevKeySecretProtector : ISecretProtector
{
    private const int KichThuocKhoa = 32; // AES-256
    private const int KichThuocNonce = 12; // AES-GCM nonce chuẩn
    private const int KichThuocTag = 16;

    private readonly string _tepKhoa;

    public DevKeySecretProtector(string directory)
    {
        Directory.CreateDirectory(directory);
        _tepKhoa = Path.Combine(directory, "dev.key");
    }

    public byte[] Protect(byte[] plain)
    {
        var khoa = LayHoacTaoKhoa();
        var nonce = RandomNumberGenerator.GetBytes(KichThuocNonce);
        var cipherText = new byte[plain.Length];
        var tag = new byte[KichThuocTag];

        using var aesGcm = new AesGcm(khoa, KichThuocTag);
        aesGcm.Encrypt(nonce, plain, cipherText, tag);

        // Định dạng lưu: nonce (12) + tag (16) + dữ liệu mã hóa.
        var ketQua = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, ketQua, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, ketQua, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, ketQua, nonce.Length + tag.Length, cipherText.Length);
        return ketQua;
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (cipher.Length < KichThuocNonce + KichThuocTag)
        {
            throw new CryptographicException("Dữ liệu mã hóa không hợp lệ.");
        }

        var khoa = LayHoacTaoKhoa();
        var nonce = cipher.AsSpan(0, KichThuocNonce).ToArray();
        var tag = cipher.AsSpan(KichThuocNonce, KichThuocTag).ToArray();
        var cipherText = cipher.AsSpan(KichThuocNonce + KichThuocTag).ToArray();
        var plain = new byte[cipherText.Length];

        using var aesGcm = new AesGcm(khoa, KichThuocTag);
        aesGcm.Decrypt(nonce, cipherText, tag, plain);
        return plain;
    }

    private byte[] LayHoacTaoKhoa()
    {
        if (File.Exists(_tepKhoa))
        {
            return Convert.FromBase64String(File.ReadAllText(_tepKhoa));
        }

        var khoaMoi = RandomNumberGenerator.GetBytes(KichThuocKhoa);
        File.WriteAllText(_tepKhoa, Convert.ToBase64String(khoaMoi));
        return khoaMoi;
    }
}
