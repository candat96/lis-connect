namespace Lis.Data.Local;

/// <summary>
/// Mã hóa/giải mã bí mật (mật khẩu CSDL...) lưu trên máy trạm.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] cipher);
}
