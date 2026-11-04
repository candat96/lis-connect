using Avalonia.Controls;

namespace Lis.App.Views;

/// <summary>
/// Cửa sổ chính: thanh điều hướng trái + nội dung. Đóng cửa sổ (nút X) chỉ ẩn xuống khay —
/// ứng dụng vẫn chạy nền; chỉ thoát thật khi chọn "Thoát" ở menu khay (xem <c>App.axaml.cs</c>,
/// nơi đặt <see cref="AllowClose"/> = true trước khi gọi Shutdown).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>true khi ứng dụng thực sự đang thoát (chọn "Thoát" ở khay) — cho Close() đi qua bình thường.</summary>
    public bool AllowClose { get; set; }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
