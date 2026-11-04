using Avalonia.Controls;
using Lis.App.Services;

namespace Lis.App.Views;

/// <summary>
/// Cửa sổ đăng nhập. Không chứa logic nghiệp vụ — xem <see cref="Lis.App.ViewModels.LoginViewModel"/>.
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        KeyDown += KeyboardFormBehavior.MoveFocusOnEnter;
    }
}
