using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Lis.App.Services;

/// <summary>
/// <see cref="IFilePicker"/> thật, dùng <c>IStorageProvider</c> của cửa sổ chính hiện tại
/// (<see cref="Application.Current"/>). Chỉ hoạt động sau khi <c>MainWindow</c> đã hiện —
/// ViewModel không giữ tham chiếu cửa sổ nào, lấy lại ở đây mỗi lần gọi.
/// </summary>
public sealed class AvaloniaFilePicker : IFilePicker
{
    public async Task<PickedFile?> PickExcelFileAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn tệp Excel danh mục",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Excel (*.xlsx)") { Patterns = ["*.xlsx"] },
            ],
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return new PickedFile(file.Name, memory.ToArray());
    }

    private static TopLevel? GetTopLevel() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
