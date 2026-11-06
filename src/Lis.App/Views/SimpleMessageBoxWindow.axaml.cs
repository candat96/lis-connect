using Avalonia.Controls;

namespace Lis.App.Views;

/// <summary>
/// Hộp thoại thông báo đơn giản (tiêu đề + nội dung + nút "Đồng ý"), dùng ở những nơi không có
/// cửa sổ chính để hiện lỗi — tệp cấu hình cục bộ hỏng, migration thất bại lúc khởi động, lỗi
/// không xử lý được rơi tới bộ xử lý toàn cục (xem <see cref="App"/>).
/// </summary>
public partial class SimpleMessageBoxWindow : Window
{
    public SimpleMessageBoxWindow()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    /// <summary>
    /// Hiện hộp thoại thông báo. Không modal (không cần cửa sổ chủ) — dùng được cả khi chưa có
    /// cửa sổ nào khác mở (vd lỗi đọc cấu hình ngay lúc khởi động) hoặc từ bộ xử lý lỗi toàn cục.
    /// </summary>
    public static void Show(string title, string message)
    {
        var window = new SimpleMessageBoxWindow();
        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.Show();
    }
}
