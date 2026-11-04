using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core;
using Lis.Data.Settings;
using Lis.Reports.Labels;
using Lis.Reports.Printing;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// Mục "Máy in" của màn Cài đặt: chọn máy in tem và máy in phiếu (lưu ở <c>setting</c> khóa
/// <see cref="SettingKeys.LabelPrinter"/>/<see cref="SettingKeys.ReportPrinter"/>), kích thước tem
/// (<see cref="SettingKeys.LabelSize"/>) và nút "In tem thử". Trên hệ không liệt kê được máy in
/// (macOS/Linux) thì hiện ô nhập tên máy in thay cho danh sách chọn.
/// </summary>
public partial class PrinterSettingsViewModel(ISettingStore settings, IPrintService printService, TimeProvider? clock = null) : ObservableObject
{
    /// <summary>Khổ tem hợp lệ (mm) cho mỗi cạnh — ngoài khoảng này gần như chắc là gõ nhầm đơn vị.</summary>
    public const double MinLabelSideMm = 10;
    public const double MaxLabelSideMm = 300;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Tem thử in từ nút "In tem thử" — dữ liệu giả theo quy ước (họ tên chứa "Mẫu").</summary>
    public static LabelData TestLabel(DateOnly day) => new("9261104001", "Nguyễn Văn Mẫu", 1980, 'M', "Máu EDTA", day);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrinters))]
    private IReadOnlyList<string> _printers = [];

    [ObservableProperty]
    private string? _labelPrinter;

    [ObservableProperty]
    private string? _reportPrinter;

    [ObservableProperty]
    private string _labelWidthText = FormatMm(LabelSize.Default.WidthMm);

    [ObservableProperty]
    private string _labelHeightText = FormatMm(LabelSize.Default.HeightMm);

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasPrinters => Printers.Count > 0;

    public string PrinterListHint =>
        "Không liệt kê được máy in trên hệ này — tem và phiếu sẽ mở bằng ứng dụng xem PDF thay vì in";

    /// <summary>
    /// Nạp danh sách máy in và giá trị đã lưu — gọi một lần khi mở màn Cài đặt. Tên máy in đã
    /// lưu mà không còn trong danh sách vẫn được thêm vào, để ComboBox giữ lựa chọn thay vì
    /// đưa về trống (lần "Lưu" sau sẽ xóa mất cài đặt).
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;

            // Liệt kê máy in có thể chậm (hỏi driver/spooler) — không chạy trên luồng giao diện.
            var printers = await Task.Run(printService.Printers);

            LabelPrinter = await settings.GetAsync(SettingKeys.LabelPrinter);
            ReportPrinter = await settings.GetAsync(SettingKeys.ReportPrinter);
            Printers = WithSavedPrinters(printers, LabelPrinter, ReportPrinter);

            var size = LabelSize.FromSetting(await settings.GetAsync(SettingKeys.LabelSize));
            LabelWidthText = FormatMm(size.WidthMm);
            LabelHeightText = FormatMm(size.HeightMm);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được cài đặt máy in: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!TryBuildLabelSize(out var size, out var sizeError))
        {
            ErrorMessage = sizeError;
            return;
        }

        IsBusy = true;
        try
        {
            await settings.SetAsync(SettingKeys.LabelPrinter, NullIfBlank(LabelPrinter));
            await settings.SetAsync(SettingKeys.ReportPrinter, NullIfBlank(ReportPrinter));
            await settings.SetAsync(SettingKeys.LabelSize, size.ToSetting());
            SuccessMessage = "Đã lưu cài đặt máy in";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lưu cài đặt máy in thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>In một tem mẫu với kích thước đang nhập (chưa cần Lưu) ra máy in tem đang chọn.</summary>
    [RelayCommand]
    private async Task PrintTestLabelAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (!TryBuildLabelSize(out var size, out var sizeError))
        {
            ErrorMessage = sizeError;
            return;
        }

        IsBusy = true;
        try
        {
            var pdf = LabelPdf.Render([TestLabel(DateOnly.FromDateTime(_clock.GetLocalNow().DateTime))], size);
            await printService.PrintAsync(pdf, NullIfBlank(LabelPrinter));
            SuccessMessage = "Đã gửi tem thử tới máy in";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"In tem thử lỗi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildLabelSize(out LabelSize size, out string? error)
    {
        size = LabelSize.Default;

        if (!DecimalInput.TryParse(LabelWidthText, out var width) || width <= 0)
        {
            error = "Chiều rộng tem phải là số lớn hơn 0 (mm)";
            return false;
        }

        if (!DecimalInput.TryParse(LabelHeightText, out var height) || height <= 0)
        {
            error = "Chiều cao tem phải là số lớn hơn 0 (mm)";
            return false;
        }

        if (!IsSaneSide(width) || !IsSaneSide(height))
        {
            error = $"Khổ tem phải từ {MinLabelSideMm:0} đến {MaxLabelSideMm:0} mm";
            return false;
        }

        size = new LabelSize((double)width, (double)height);
        error = null;
        return true;
    }

    private static bool IsSaneSide(decimal mm) => mm >= (decimal)MinLabelSideMm && mm <= (decimal)MaxLabelSideMm;

    private static IReadOnlyList<string> WithSavedPrinters(IReadOnlyList<string> printers, params string?[] saved)
    {
        if (printers.Count == 0)
        {
            return printers;
        }

        var list = printers.ToList();
        foreach (var name in saved)
        {
            if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string FormatMm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
