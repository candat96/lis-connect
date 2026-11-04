using Avalonia.Controls;
using Avalonia.Input;

namespace Lis.App.Services;

/// <summary>
/// Hành vi bàn phím dùng chung cho các form nhập liệu (theo nguyên tắc "tối ưu bàn phím"
/// của LIS Connect): Enter chuyển sang ô kế tiếp (trừ khi đang ở nút, nơi Enter đã có
/// nghĩa là bấm nút), Esc đóng cửa sổ (coi cửa sổ cài đặt/đăng nhập như một hộp thoại).
/// </summary>
public static class KeyboardFormBehavior
{
    /// <summary>Gắn vào sự kiện KeyDown của cửa sổ (<c>window.KeyDown += KeyboardFormBehavior.OnKeyDown;</c>).</summary>
    public static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            window.Close();
            return;
        }

        if (e.Key != Key.Enter)
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
