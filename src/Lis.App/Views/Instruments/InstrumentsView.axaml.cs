using Avalonia.Controls;
using Avalonia.Interactivity;
using Lis.App.ViewModels.Instruments;

namespace Lis.App.Views.Instruments;

/// <summary>Màn "Máy &amp; map". Nút "Mở bảng map kênh" mở <see cref="ChannelMapWindow"/> cho máy đang chọn.</summary>
public partial class InstrumentsView : UserControl
{
    public InstrumentsView()
    {
        InitializeComponent();
        OpenChannelMapButton.Click += OnOpenChannelMapClick;
    }

    private async void OnOpenChannelMapClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstrumentsViewModel viewModel || !viewModel.ChannelEditor.HasInstrument)
        {
            return;
        }

        var window = new ChannelMapWindow { DataContext = viewModel.ChannelEditor };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }
}
