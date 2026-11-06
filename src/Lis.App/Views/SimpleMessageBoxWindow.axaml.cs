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

    /// <summary>
    /// Giống <see cref="Show(string, string)"/> nhưng thay nút "Đồng ý" mặc định bằng các nút
    /// hành động tùy chỉnh — dùng khi cần người dùng chọn giữa nhiều lựa chọn (vd lỗi CSDL lúc
    /// khởi động: "Thử lại" / "Cài đặt lại kết nối") thay vì chỉ đóng thông báo. Mỗi nút chạy
    /// hành động của nó TRƯỚC khi đóng cửa sổ này — nếu hành động mở một cửa sổ khác, cửa sổ đó
    /// đã hiện trước khi hộp thoại này đóng, tránh khoảnh khắc không còn cửa sổ nào mở khiến
    /// <c>ShutdownMode.OnLastWindowClose</c> thoát ứng dụng (cùng lý do như App.ShowSetupWizard).
    /// </summary>
    public static void ShowWithActions(string title, string message, params (string Label, Action OnClick)[] actions)
    {
        var window = new SimpleMessageBoxWindow();
        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.ButtonsPanel.Children.Clear();

        foreach (var (label, onClick) in actions)
        {
            var button = new Button
            {
                Content = label,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                MinWidth = 100,
            };
            button.Classes.Add("primary");
            button.Click += (_, _) =>
            {
                onClick();
                window.Close();
            };
            window.ButtonsPanel.Children.Add(button);
        }

        window.Show();
    }
}
