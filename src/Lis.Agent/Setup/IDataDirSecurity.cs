using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lis.Agent;

/// <summary>Khoá quyền thư mục dữ liệu <c>%ProgramData%\LIS Agent</c> — tách ra để logic cài đặt kiểm thử được ngoài Windows.</summary>
public interface IDataDirSecurity
{
    void Secure(string directory);
}

public sealed class NoDataDirSecurity : IDataDirSecurity
{
    public static readonly NoDataDirSecurity Instance = new();

    public void Secure(string directory)
    {
    }
}

/// <summary>
/// Chỉ SYSTEM + Administrators có toàn quyền, không kế thừa từ ProgramData, người dùng thường không có quyền gì:
/// họ không tạo được tệp trong <c>cho-gui</c> hay dựng sẵn <c>cach-ly</c> làm junction. Chạy khi đã nâng quyền.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataDirSecurity : IDataDirSecurity
{
    public void Secure(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
