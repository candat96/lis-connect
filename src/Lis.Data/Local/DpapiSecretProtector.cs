using System.Security.Cryptography;

namespace Lis.Data.Local;

/// <summary>
/// Mã hóa bí mật bằng DPAPI của Windows, phạm vi <see cref="DataProtectionScope.LocalMachine"/>
/// để dịch vụ nền (chạy dưới tài khoản hệ thống) vẫn giải mã được, không chỉ riêng người dùng đăng nhập.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DpapiSecretProtector chỉ chạy được trên Windows.");
        }

        return ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DpapiSecretProtector chỉ chạy được trên Windows.");
        }

        return ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.LocalMachine);
    }
}
