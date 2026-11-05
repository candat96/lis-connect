using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Lis.App.ViewModels.Settings;

namespace Lis.App.Views.Settings;

/// <summary>
/// Tab "Thông tin cơ sở" của màn Cài đặt. Chọn tệp logo ở code-behind vì
/// <c>IStorageProvider</c> cần một <c>TopLevel</c> (cửa sổ) — ViewModel không nắm cửa sổ.
/// </summary>
public partial class FacilityInfoView : UserControl
{
    public FacilityInfoView()
    {
        InitializeComponent();
        PickLogoButton.Click += OnPickLogoClickedAsync;
    }

    private async void OnPickLogoClickedAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FacilitySettingsViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn ảnh logo",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Ảnh") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        viewModel.SetLogo(memory.ToArray(), file.Name);
    }
}
