using Lis.Core.Auth;

namespace Lis.Data.Auth;

/// <summary>
/// Phiên đăng nhập hiện tại của ứng dụng: người dùng đang đăng nhập và quyền của họ.
/// </summary>
public sealed class CurrentUser(UserInfo user)
{
    public UserInfo User { get; } = user;

    /// <summary>
    /// Người dùng hiện tại có quyền <paramref name="permission"/> không (xét theo tất cả vai trò của họ).
    /// </summary>
    public bool Can(Permission permission) => Permissions.Has(User.Roles, permission);
}
