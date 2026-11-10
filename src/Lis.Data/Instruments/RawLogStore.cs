using System.Globalization;
using System.Text;
using Lis.Core.Instruments;

namespace Lis.Data.Instruments;

/// <summary>Đọc/ghi log thô của từng máy. Cài đặt thật là <see cref="RawLogStore"/>; kiểm thử dùng bản giả.</summary>
public interface IRawLogStore
{
    /// <summary>Thư mục gốc chứa log (để mở bằng Explorer).</summary>
    string RootDirectory { get; }

    void Append(Guid instrumentId, RawLogEntry entry);

    /// <summary>Các ngày có log của máy, mới nhất trước.</summary>
    IReadOnlyList<DateOnly> ListDates(Guid instrumentId);

    /// <summary>Toàn bộ bản ghi của máy trong một ngày, theo thứ tự ghi.</summary>
    IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date);

    /// <summary>Đường dẫn tệp log của máy trong ngày (có thể chưa tồn tại).</summary>
    string FilePath(Guid instrumentId, DateOnly date);

    /// <summary>Xóa tệp log cũ hơn <paramref name="keepDays"/> ngày; trả số tệp đã xóa.</summary>
    int DeleteOlderThan(DateOnly today, int keepDays) => 0;

    /// <summary>Tổng dung lượng log thô (byte).</summary>
    long TotalBytes() => 0;
}

/// <summary>
/// Log thô trên đĩa máy trạm: <c>&lt;gốc&gt;\&lt;id máy&gt;\yyyy-MM-dd.listrace</c>, mỗi máy mỗi ngày
/// một tệp, ghi nối tiếp (định dạng ở <see cref="RawLogFormat"/>). Ghi trên đĩa thay vì CSDL để
/// không phụ thuộc mạng/CSDL. Thư mục theo id máy để đổi tên máy không mất log.
/// </summary>
/// <remarks>
/// Ngày của tệp theo giờ địa phương của bản ghi. Ghi khóa theo từng tệp nên an toàn khi nhiều
/// luồng (nhiều cổng) cùng ghi; đọc mở với <see cref="FileShare.ReadWrite"/> để xem được tệp đang ghi.
/// </remarks>
public sealed class RawLogStore(string rootDirectory) : IRawLogStore
{
    private static readonly object WriteLock = new();

    /// <summary><c>%ProgramData%\LIS Connect\rawlog</c> (macOS/Linux dev: thư mục tương ứng của hệ điều hành).</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LIS Connect", "rawlog");

    public string RootDirectory { get; } = rootDirectory;

    public void Append(Guid instrumentId, RawLogEntry entry)
    {
        var path = FilePath(instrumentId, DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime));
        var line = RawLogFormat.Serialize(entry) + "\n";
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
        }
    }

    public IReadOnlyList<DateOnly> ListDates(Guid instrumentId)
    {
        var directory = InstrumentDirectory(instrumentId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.listrace")
            .Select(file => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .OrderDescending()
            .ToList();
    }

    public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date)
    {
        var path = FilePath(instrumentId, date);
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var entries = new List<RawLogEntry>();
        while (reader.ReadLine() is { } line)
        {
            if (RawLogFormat.TryParse(line) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    public string FilePath(Guid instrumentId, DateOnly date) =>
        Path.Combine(InstrumentDirectory(instrumentId), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".listrace");

    /// <summary>
    /// Xóa các tệp log có ngày (theo tên tệp) cũ hơn <paramref name="keepDays"/> ngày tính đến
    /// <paramref name="today"/> — ngày hôm nay và <c>keepDays - 1</c> ngày trước được giữ. Tệp
    /// đang mở bởi tiến trình khác hoặc không xóa được thì bỏ qua, lần sau xóa tiếp. Trả số tệp đã xóa.
    /// </summary>
    public int DeleteOlderThan(DateOnly today, int keepDays)
    {
        if (keepDays < 1 || !Directory.Exists(RootDirectory))
        {
            return 0;
        }

        var cutoff = today.AddDays(-(keepDays - 1));
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*.listrace", SearchOption.AllDirectories))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tệp đang bị khóa — để lần dọn sau.
            }
        }

        return deleted;
    }

    /// <summary>Tổng dung lượng (byte) các tệp log thô trên máy trạm này.</summary>
    public long TotalBytes()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*.listrace", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Tệp vừa bị xóa giữa chừng.
            }
        }

        return total;
    }

    private string InstrumentDirectory(Guid instrumentId) => Path.Combine(RootDirectory, instrumentId.ToString("N"));
}
