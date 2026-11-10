using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lis.App.ViewModels.Instruments;

namespace Lis.App.Views.Instruments;

/// <summary>
/// Màn "Log thô". Phần View chỉ lo việc thuần giao diện: cuộn xuống cuối khi có dòng mới lúc
/// theo dõi trực tiếp, chép các dòng đang hiện vào clipboard, và hộp thoại lưu/mở tệp <c>.listrace</c>
/// (nội dung tệp do ViewModel dựng/đọc).
/// </summary>
public partial class RawLogView : UserControl
{
    private RawLogViewModel? _viewModel;

    public RawLogView()
    {
        InitializeComponent();
        CopyButton.Click += OnCopyClick;
        ExportButton.Click += OnExportClick;
        ImportButton.Click += OnImportClick;
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

    private static readonly FilePickerFileType ListraceType = new("Log thô LIS (.listrace)") { Patterns = ["*.listrace"] };

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Xuất log thô — tệp CHỨA DỮ LIỆU NGƯỜI BỆNH",
                SuggestedFileName = _viewModel.SuggestedExportName,
                DefaultExtension = "listrace",
                FileTypeChoices = [ListraceType],
            });
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(_viewModel.BuildListrace());
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Xuất tệp lỗi: {ex.Message}";
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Mở tệp log thô",
                AllowMultiple = false,
                FileTypeFilter = [ListraceType, FilePickerFileTypes.All],
            });
            if (files.Count == 0)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            _viewModel.ImportListrace(files[0].Name, await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            _viewModel.ErrorMessage = $"Mở tệp lỗi: {ex.Message}";
        }
    }
}
