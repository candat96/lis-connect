using Avalonia.Controls;
using Avalonia.Input;
using Lis.App.ViewModels.Unmatched;

namespace Lis.App.Views.Unmatched;

/// <summary>
/// Màn Chưa ghép. Enter trên ô barcode của ô "Gán vào mẫu…" tra mẫu; Enter trên ô lý do bỏ
/// qua xác nhận bỏ qua. Các phím này xử lý riêng ở đây (đánh dấu Handled) nên không chạm tới
/// <see cref="Lis.App.Services.KeyboardFormBehavior"/> gắn ở <c>MainWindow</c>.
/// </summary>
public partial class UnmatchedView : UserControl
{
    public UnmatchedView()
    {
        InitializeComponent();
        AssignBarcodeBox.KeyDown += OnAssignBarcodeKeyDown;
        DismissReasonBox.KeyDown += OnDismissReasonKeyDown;
    }

    private UnmatchedViewModel? ViewModel => DataContext as UnmatchedViewModel;

    private void OnAssignBarcodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.AssignPanel.LookupCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDismissReasonKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.ConfirmDismissCommand.Execute(null);
        e.Handled = true;
    }
}
