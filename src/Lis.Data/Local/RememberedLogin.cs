using System.Text;

namespace Lis.Data.Local;

/// <summary>
/// "Ghi nhớ đăng nhập" trên máy này: lưu id tài khoản + dấu phiên (băm của password_hash, xem
/// <c>AuthService.SessionStampAsync</c>) vào một tệp được mã hoá theo tài khoản Windows
/// (<see cref="ISecretProtector"/>). Lần mở sau, nếu tài khoản còn hoạt động và mật khẩu chưa
/// đổi thì vào thẳng màn chính. Không lưu mật khẩu.
/// </summary>
public sealed class RememberedLogin(string directory, ISecretProtector protector)
{
    private string FilePath => Path.Combine(directory, "dang-nhap.bin");

    public void Save(Guid userId, string stamp)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(FilePath, protector.Protect(Encoding.UTF8.GetBytes($"{userId:N}|{stamp}")));
    }

    /// <summary>Đọc phiên đã ghi nhớ; tệp không có, hỏng, hoặc của tài khoản Windows khác → null.</summary>
    public (Guid UserId, string Stamp)? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var parts = Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(FilePath))).Split('|', 2);
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var userId) ? (userId, parts[1]) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Không xoá được thì lần sau TryLoad vẫn phải khớp CSDL mới vào được.
        }
    }
}
