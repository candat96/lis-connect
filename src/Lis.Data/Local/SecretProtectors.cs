namespace Lis.Data.Local;

/// <summary>
/// Chọn cách mã hóa bí mật phù hợp theo hệ điều hành hiện tại.
/// </summary>
public static class SecretProtectors
{
    /// <summary>
    /// Trả về <see cref="DpapiSecretProtector"/> trên Windows, hoặc <see cref="DevKeySecretProtector"/>
    /// (dùng khóa riêng lưu trong <paramref name="directory"/>) trên các hệ điều hành khác.
    /// </summary>
    public static ISecretProtector ForCurrentOs(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretProtector();
        }

        return new DevKeySecretProtector(directory);
    }
}

/// <summary>
/// Các đường dẫn thư mục dữ liệu cục bộ của LIS Connect theo hệ điều hành.
/// </summary>
public static class LocalPaths
{
    /// <summary>
    /// Thư mục lưu cấu hình/dữ liệu cục bộ: <c>%ProgramData%\LIS Connect</c> trên Windows,
    /// <c>~/Library/Application Support/LIS Connect</c> trên macOS.
    /// </summary>
    public static string DataDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LIS Connect")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "LIS Connect");
}
