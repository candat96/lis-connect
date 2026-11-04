namespace Lis.Core.Auth;

/// <summary>
/// Các quyền thao tác trong hệ thống.
/// </summary>
public enum Permission
{
    QuanTri,
    TiepNhan,
    XemKetQua,
    NhapSuaKetQua,
    Duyet,
    HuyDuyet,
    GhepTay,
    TheoDoiMay,
    XemLogTho,
}

/// <summary>
/// Tra quyền theo vai trò. Vai trò <see cref="Roles.QuanTri"/> có tất cả quyền;
/// các vai trò khác chỉ có tập quyền cố định theo quy định.
/// </summary>
public static class Permissions
{
    private static readonly IReadOnlyList<Permission> KyThuatVienPermissions =
    [
        Permission.XemKetQua,
        Permission.NhapSuaKetQua,
        Permission.Duyet,
        Permission.GhepTay,
        Permission.TheoDoiMay,
        Permission.XemLogTho,
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Permission>> ByRole =
        new Dictionary<string, IReadOnlyList<Permission>>
        {
            [Roles.QuanTri] = Enum.GetValues<Permission>(),
            [Roles.TiepNhan] = [Permission.TiepNhan, Permission.XemKetQua],
            [Roles.KyThuatVien] = KyThuatVienPermissions,
            [Roles.TruongKhoa] = [.. KyThuatVienPermissions, Permission.HuyDuyet],
        };

    /// <summary>
    /// Kiểm tra một người dùng (có thể nhiều vai trò) có quyền <paramref name="permission"/> không.
    /// </summary>
    public static bool Has(IEnumerable<string> roles, Permission permission) =>
        roles.Any(role => ByRole.TryGetValue(role, out var permissions) && permissions.Contains(permission));
}
