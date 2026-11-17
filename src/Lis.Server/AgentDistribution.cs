using System.Security.Cryptography;

namespace Lis.Server;

/// <summary>Bản cài agent hiện có: đường dẫn tệp, phiên bản (nội dung <c>version.txt</c>, có thể null), SHA-256 hex chữ thường và kích thước.</summary>
public sealed record AgentPackage(string Path, string? Version, string Sha256, long Size);

/// <summary>
/// Tệp <c>LisAgent.exe</c> trong thư mục <c>AGENT_DIST_DIR</c> (cấu hình <c>Agent:DistDir</c>). Chưa cấu hình hoặc thiếu tệp → <see cref="Current"/> trả null.
/// SHA-256 chỉ tính lại khi độ dài hoặc thời điểm sửa tệp đổi; truy cập đồng thời được khoá.
/// </summary>
public sealed class AgentDistribution
{
    public const string InstallerName = "LisAgent.exe";
    public const string VersionFileName = "version.txt";

    private readonly object _lock = new();
    private readonly string? _distDir;
    private (long Length, DateTime LastWriteUtc, string Sha256)? _hash;

    /// <summary>Thư mục tuyệt đối hoá ngay lúc khởi tạo: <c>Results.File</c> coi đường dẫn tương đối là đường ảo dưới web root.</summary>
    public AgentDistribution(string? distDir) =>
        _distDir = string.IsNullOrWhiteSpace(distDir) ? null : Path.GetFullPath(distDir);

    public AgentPackage? Current()
    {
        if (_distDir is null) return null;
        var exe = Path.Combine(_distDir, InstallerName);
        try
        {
            if (!File.Exists(exe)) return null;

            var info = new FileInfo(exe);
            var length = info.Length;
            var lastWriteUtc = info.LastWriteTimeUtc;
            string sha256;
            lock (_lock)
            {
                if (_hash is not { } cached || cached.Length != length || cached.LastWriteUtc != lastWriteUtc)
                {
                    using var stream = File.OpenRead(exe);
                    cached = (length, lastWriteUtc, Convert.ToHexStringLower(SHA256.HashData(stream)));
                    _hash = cached;
                }

                sha256 = cached.Sha256;
            }

            return new AgentPackage(exe, ReadVersion(_distDir), sha256, length);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Tệp bị xoá giữa lúc kiểm tra và lúc đọc: coi như chưa có tệp (404), không phải lỗi 500.
            return null;
        }
    }

    /// <summary>Nội dung <c>version.txt</c> đã cắt khoảng trắng; thiếu tệp hoặc rỗng → null.</summary>
    private static string? ReadVersion(string dir)
    {
        var path = Path.Combine(dir, VersionFileName);
        if (!File.Exists(path)) return null;
        var version = File.ReadAllText(path).Trim();
        return version.Length == 0 ? null : version;
    }
}
