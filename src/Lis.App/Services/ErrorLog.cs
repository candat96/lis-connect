using Lis.Data.Local;

namespace Lis.App.Services;

/// <summary>
/// Ghi lỗi không bắt được (crash) vào <c>&lt;LocalPaths.DataDirectory&gt;/loi.log</c> — bản đợt 1
/// chưa có màn xem nhật ký trong ứng dụng (xem CHAY-THU-WINDOWS.md), nên đây là nơi duy nhất
/// người dùng/hỗ trợ kỹ thuật có thể lấy thông tin lỗi sau khi ứng dụng gặp sự cố. Chỉ ghi
/// thời điểm + nội dung ngoại lệ (.ToString(), gồm stack trace) — không bao giờ ghi dữ liệu
/// người bệnh.
/// </summary>
public static class ErrorLog
{
    private static readonly object WriteLock = new();

    public static string LogFile => Path.Combine(LocalPaths.DataDirectory, "loi.log");

    public static void Append(string context, Exception exception)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(LocalPaths.DataDirectory);
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(LogFile, line);
            }
        }
        catch
        {
            // Ghi log thất bại (vd hết dung lượng đĩa, không có quyền) không được làm hỏng thêm
            // luồng xử lý lỗi — đây đã là nhánh dự phòng cuối cùng.
        }
    }
}
