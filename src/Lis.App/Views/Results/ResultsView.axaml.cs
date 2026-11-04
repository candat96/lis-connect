using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lis.App.ViewModels.Results;

namespace Lis.App.Views.Results;

/// <summary>
/// Màn Kết quả. Trên ô giá trị: Enter ghi giá trị rồi chuyển xuống ô giá trị kế tiếp (nếu
/// còn thiếu lý do thì nhảy sang ô lý do), Esc trả lại giá trị đã lưu. Enter trên ô lý do ghi
/// dòng đang sửa. Enter trên ô tìm kiếm lọc lại danh sách. F10 duyệt, F11 in phiếu. Các phím
/// này xử lý riêng ở đây (đánh dấu Handled) nên không chạm tới
/// <see cref="Lis.App.Services.KeyboardFormBehavior"/> gắn ở <c>MainWindow</c>.
/// </summary>
public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;
        ReasonBox.KeyDown += OnReasonKeyDown;
        UnapproveReasonBox.KeyDown += OnUnapproveReasonKeyDown;
        KeyDown += OnViewKeyDown;
    }

    private ResultsViewModel? ViewModel => DataContext as ResultsViewModel;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ResultRowViewModel row || ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CancelEdit(row);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await viewModel.CommitValueAsync(row);

        if (row.IsDirty && viewModel.IsReasonVisible)
        {
            ReasonBox.Focus();
            return;
        }

        FocusNextValueBox(row);
    }

    private async void OnReasonKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
        {
            return;
        }

        e.Handled = true;
        var dirtyRow = viewModel.Rows.FirstOrDefault(r => r.IsDirty);
        if (dirtyRow is null)
        {
            return;
        }

        await viewModel.CommitValueAsync(dirtyRow);
        if (!dirtyRow.IsDirty)
        {
            FocusNextValueBox(dirtyRow);
        }
    }

    private void OnUnapproveReasonKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.ConfirmUnapproveCommand.Execute(null);
        e.Handled = true;
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F10:
                if (viewModel.ApproveCommand.CanExecute(null))
                {
                    viewModel.ApproveCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Key.F11:
                if (viewModel.PrintCommand.CanExecute(null))
                {
                    viewModel.PrintCommand.Execute(null);
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>Chuyển focus tới ô giá trị sửa được kế tiếp sau <paramref name="row"/> (bỏ qua dòng panel và dòng không sửa được).</summary>
    private void FocusNextValueBox(ResultRowViewModel row)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var start = viewModel.Rows.IndexOf(row);
        for (var index = start + 1; index < viewModel.Rows.Count; index++)
        {
            if (!viewModel.Rows[index].IsEditable)
            {
                continue;
            }

            var box = RowsList.ContainerFromIndex(index)?
                .GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "ValueBox");

            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                return;
            }
        }
    }
}
