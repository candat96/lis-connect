using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lis.App.ViewModels.Instruments;

namespace Lis.App.Views.Instruments;

/// <summary>
/// Cửa sổ map kênh cỡ lớn của một máy (mở từ màn "Máy &amp; map"). Enter trên ô mã chỉ số lưu
/// luôn dòng đang nhập (nhập mã + Enter).
/// </summary>
public partial class ChannelMapWindow : Window
{
    public ChannelMapWindow()
    {
        InitializeComponent();
        NewServiceCodeBox.KeyDown += OnNewServiceCodeKeyDown;
    }

    private void OnNewServiceCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ChannelMapEditorViewModel editor)
        {
            return;
        }

        editor.AddChannelCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
