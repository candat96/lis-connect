using Avalonia.Controls;
using Avalonia.Input;
using Lis.App.ViewModels.Instruments;

namespace Lis.App.Views.Instruments;

/// <summary>
/// Màn "Máy &amp; map". Enter trên ô mã chỉ số của dòng map kênh mới thêm luôn dòng đó
/// (nguyên tắc "tối ưu bàn phím": nhập mã + Enter) — riêng cho ô này, khác với
/// <see cref="Lis.App.Services.KeyboardFormBehavior"/> (chỉ chuyển ô, gắn ở <c>MainWindow</c>).
/// </summary>
public partial class InstrumentsView : UserControl
{
    public InstrumentsView()
    {
        InitializeComponent();
        NewServiceCodeBox.KeyDown += OnNewServiceCodeKeyDown;
    }

    private void OnNewServiceCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || NewServiceCodeBox.DataContext is not ChannelMapEditorViewModel channelEditor)
        {
            return;
        }

        channelEditor.AddChannelCommand.Execute(null);
        e.Handled = true;
    }
}
