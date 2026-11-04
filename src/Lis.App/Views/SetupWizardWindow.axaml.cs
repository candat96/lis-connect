using Avalonia.Controls;
using Lis.App.Services;

namespace Lis.App.Views;

/// <summary>
/// Cửa sổ trình hướng dẫn cài đặt lần đầu (3 bước, mục 5 bản thiết kế). Không chứa logic
/// nghiệp vụ — mọi xử lý nằm ở <see cref="Lis.App.ViewModels.SetupWizardViewModel"/>.
/// </summary>
public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
        KeyDown += KeyboardFormBehavior.MoveFocusOnEnter;
    }
}
