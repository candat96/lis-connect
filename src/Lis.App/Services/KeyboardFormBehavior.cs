using Avalonia.Controls;
using Avalonia.Input;

namespace Lis.App.Services;

/// <summary>
/// Hành vi bàn phím dùng chung cho các form nhập liệu (theo nguyên tắc "tối ưu bàn phím"
/// của LIS Connect): Enter chuyển sang ô kế tiếp (trừ khi đang ở nút, nơi Enter đã có
/// nghĩa là bấm nút). Không xử lý Esc ở đây — Esc chỉ nên đóng các hộp thoại thật sự
/// (cửa sổ phụ, có cửa sổ khác phía sau); các cửa sổ chính của luồng khởi động (cài đặt,
/// đăng nhập) không phải hộp thoại, đóng chúng bằng Esc sẽ làm ứng dụng ẩn mất không có
/// cách mở lại (không có icon khay hữu dụng tới lúc đó).
/// </summary>
public static class KeyboardFormBehavior
{
    /// <summary>Gắn vào sự kiện KeyDown của cửa sổ (<c>window.KeyDown += KeyboardFormBehavior.MoveFocusOnEnter;</c>).</summary>
    public static void MoveFocusOnEnter(object? sender, KeyEventArgs e)
    {
        if (sender is not Window window || e.Key != Key.Enter)
        {
            return;
        }

        var focusManager = window.FocusManager;
        if (focusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or CheckBox)
        {
            focusManager.TryMoveFocus(NavigationDirection.Next);
            e.Handled = true;
        }
    }
}
