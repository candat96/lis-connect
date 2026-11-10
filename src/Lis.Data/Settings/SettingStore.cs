using Dapper;

namespace Lis.Data.Settings;

/// <summary>Các khóa dùng trong bảng <c>setting</c>. Khai một chỗ để màn Cài đặt và nơi đọc dùng cùng tên.</summary>
public static class SettingKeys
{
    /// <summary>Kích thước tem, dạng <c>"50x25"</c> (mm) — xem <c>Lis.Reports.Labels.LabelSize</c>.</summary>
    public const string LabelSize = "label.size";

    /// <summary>Tên máy in tem; thiếu thì dùng máy in mặc định.</summary>
    public const string LabelPrinter = "printer.label";

    /// <summary>Tên máy in phiếu kết quả; thiếu thì dùng máy in mặc định.</summary>
    public const string ReportPrinter = "printer.report";

    /// <summary>Số ngày giữ log thô trên đĩa máy trạm (mặc định <see cref="RawLogDefaults.KeepDays"/>).</summary>
    public const string RawLogKeepDays = "rawlog.keep_days";

    /// <summary>Ngưỡng cảnh báo dung lượng log thô, tính bằng MB (mặc định <see cref="RawLogDefaults.WarnMegabytes"/>).</summary>
    public const string RawLogWarnMegabytes = "rawlog.warn_mb";
}

/// <summary>Giá trị mặc định của cấu hình log thô (thiết kế mục 10).</summary>
public static class RawLogDefaults
{
    public const int KeepDays = 30;

    public const int WarnMegabytes = 1024;

    /// <summary>Đọc số nguyên dương từ chuỗi cài đặt; thiếu/sai → <paramref name="fallback"/>.</summary>
    public static int Parse(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var n) && n > 0 ? n : fallback;
}

/// <summary>Đọc/ghi cấu hình khóa/giá trị dùng chung toàn hệ thống (bảng <c>setting</c>).</summary>
public interface ISettingStore
{
    Task<string?> GetAsync(string key);

    /// <summary>Ghi giá trị (tạo mới hoặc ghi đè). <c>null</c> giữ khóa nhưng xóa giá trị.</summary>
    Task SetAsync(string key, string? value);
}

/// <summary>Nơi duy nhất chạm vào bảng <c>setting</c>.</summary>
public sealed class SettingStore(Db db) : ISettingStore
{
    public async Task<string?> GetAsync(string key)
    {
        await using var connection = await db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT value FROM setting WHERE key = @key",
            new { key });
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO setting (key, value)
            VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value
            """,
            new { key, value });
    }
}
