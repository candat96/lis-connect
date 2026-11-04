namespace Lis.Core.Auth;

/// <summary>
/// Mã vai trò cố định của hệ thống (lưu trong cột <c>roles</c> của bảng app_user).
/// </summary>
public static class Roles
{
    public const string QuanTri = "quan-tri";
    public const string TiepNhan = "tiep-nhan";
    public const string KyThuatVien = "ky-thuat-vien";
    public const string TruongKhoa = "truong-khoa";

    /// <summary>
    /// Toàn bộ mã vai trò hợp lệ.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [QuanTri, TiepNhan, KyThuatVien, TruongKhoa];
}
