using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Lis.App.ViewModels.Instruments;

namespace Lis.App.Views.Instruments;

/// <summary>
/// Màn "Log thô". Phần View chỉ lo việc thuần giao diện: cuộn xuống cuối khi có dòng mới lúc
/// theo dõi trực tiếp, và chép các dòng đang hiện vào clipboard.
/// </summary>
public partial class RawLogView : UserControl
{
    private RawLogViewModel? _viewModel;

    public RawLogView()
    {
        InitializeComponent();
        CopyButton.Click += OnCopyClick;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.RowsAppended -= OnRowsAppended;
            }

            _viewModel = DataContext as RawLogViewModel;
            if (_viewModel is not null)
            {
                _viewModel.RowsAppended += OnRowsAppended;
            }
        };
    }

    private void OnRowsAppended(object? sender, EventArgs e)
    {
        if (_viewModel is { Rows.Count: > 0 } viewModel)
        {
            LogList.ScrollIntoView(viewModel.Rows.Count - 1);
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(_viewModel.BuildCopyText());
    }
}
