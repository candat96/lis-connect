using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.App.Services;
using Lis.Data.Instruments;
using Lis.Data.Settings;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// Tab "Log thô" của màn Cài đặt: số ngày giữ log trên đĩa máy trạm, ngưỡng cảnh báo dung lượng,
/// dung lượng đang dùng và nút "Dọn ngay" (thiết kế mục 10).
/// </summary>
public partial class RawLogSettingsViewModel(ISettingStore settings, IRawLogStore logs, TimeProvider? clock = null) : ObservableObject
{
    public const int MaxKeepDays = 3650;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [ObservableProperty]
    private string _keepDaysText = RawLogDefaults.KeepDays.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _warnMegabytesText = RawLogDefaults.WarnMegabytes.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageText))]
    [NotifyPropertyChangedFor(nameof(IsOverThreshold))]
    private long _usedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverThreshold))]
    private int _savedWarnMegabytes = RawLogDefaults.WarnMegabytes;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    public string Folder => logs.RootDirectory;

    public string UsageText => $"Đang dùng {FormatSize(UsedBytes)} trên máy trạm này";

    public bool IsOverThreshold => UsedBytes > SavedWarnMegabytes * 1024L * 1024L;

    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            var keep = RawLogDefaults.Parse(await settings.GetAsync(SettingKeys.RawLogKeepDays), RawLogDefaults.KeepDays);
            var warn = RawLogDefaults.Parse(await settings.GetAsync(SettingKeys.RawLogWarnMegabytes), RawLogDefaults.WarnMegabytes);
            KeepDaysText = keep.ToString(CultureInfo.InvariantCulture);
            WarnMegabytesText = warn.ToString(CultureInfo.InvariantCulture);
            SavedWarnMegabytes = warn;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được cài đặt log thô: {ex.Message}";
        }

        await RefreshUsageAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!int.TryParse(KeepDaysText?.Trim(), out var keep) || keep < 1 || keep > MaxKeepDays)
        {
            ErrorMessage = $"Số ngày giữ log phải là số nguyên từ 1 đến {MaxKeepDays}";
            return;
        }

        if (!int.TryParse(WarnMegabytesText?.Trim(), out var warn) || warn < 1)
        {
            ErrorMessage = "Ngưỡng cảnh báo phải là số nguyên MB lớn hơn 0";
            return;
        }

        try
        {
            await settings.SetAsync(SettingKeys.RawLogKeepDays, keep.ToString(CultureInfo.InvariantCulture));
            await settings.SetAsync(SettingKeys.RawLogWarnMegabytes, warn.ToString(CultureInfo.InvariantCulture));
            SavedWarnMegabytes = warn;
            SuccessMessage = "Đã lưu cài đặt log thô";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lưu cài đặt log thô thất bại: {ex.Message}";
        }
    }

    /// <summary>Dọn ngay theo số ngày đã lưu.</summary>
    [RelayCommand]
    private async Task CleanNowAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var deleted = await RawLogJanitor.RunOnceAsync(settings, logs, _clock);
            SuccessMessage = deleted == 0 ? "Không có tệp log nào quá hạn" : $"Đã xóa {deleted} tệp log quá hạn";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Dọn log thất bại: {ex.Message}";
        }

        await RefreshUsageAsync();
    }

    private async Task RefreshUsageAsync()
    {
        try
        {
            UsedBytes = await Task.Run(logs.TotalBytes);
        }
        catch (Exception)
        {
            UsedBytes = 0;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
