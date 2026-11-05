using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Orders;
using Lis.Data.Facility;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// Mục "Thông tin cơ sở" và "Barcode &amp; tem" của màn Cài đặt — cả hai đọc/ghi cùng
/// <see cref="FacilityInfo"/> (bảng <c>facility</c>, luôn một dòng) nên dùng chung một
/// ViewModel để tránh ghi đè lẫn nhau khi lưu.
/// </summary>
public partial class FacilitySettingsViewModel(IFacilityStore facility, TimeProvider? clock = null) : ObservableObject
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Thông tin cơ sở.
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private string _phone = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    private byte[]? _logoBytes;

    [ObservableProperty]
    private string? _logoFileName;

    public bool HasLogo => LogoBytes is { Length: > 0 };

    // Barcode & tem.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodePreview))]
    private string _barcodePrefix = "9";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodePreview))]
    private string _barcodeDatePattern = "yyMMdd";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodePreview))]
    private string _barcodeSeqDigitsText = "3";

    /// <summary>true: cả phiếu một barcode (<see cref="SampleGrouper.Single"/>); false (mặc định): mỗi loại mẫu một barcode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodePreview))]
    private bool _barcodeModeSingle;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Barcode mẫu sinh từ cấu hình đang gõ (chưa cần Lưu) — kèm số chữ số để người dùng thấy ngay độ dài.</summary>
    public string BarcodePreview
    {
        get
        {
            if (!int.TryParse(BarcodeSeqDigitsText, out var seqDigits) || seqDigits <= 0)
            {
                return "Số chữ số thứ tự không hợp lệ";
            }

            try
            {
                var format = new BarcodeFormat(BarcodePrefix, BarcodeDatePattern, seqDigits, BarcodeMode);
                var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
                var sample = BarcodeComposer.Compose(format, today, 1);
                return $"{sample} ({sample.Length} chữ số)";
            }
            catch (Exception ex)
            {
                return $"Mẫu barcode không hợp lệ: {ex.Message}";
            }
        }
    }

    private string BarcodeMode => BarcodeModeSingle ? SampleGrouper.Single : SampleGrouper.PerSpecimen;

    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            var info = await facility.GetAsync();
            if (info is null)
            {
                return;
            }

            Name = info.Name;
            Address = info.Address ?? "";
            Phone = info.Phone ?? "";
            LogoBytes = info.Logo;
            BarcodePrefix = info.BarcodePrefix;
            BarcodeDatePattern = info.BarcodeDatePattern;
            BarcodeSeqDigitsText = info.BarcodeSeqDigits.ToString();
            BarcodeModeSingle = info.BarcodeMode == SampleGrouper.Single;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không nạp được thông tin cơ sở: {ex.Message}";
        }
    }

    /// <summary>Gọi từ code-behind sau khi người dùng chọn tệp ảnh ở hộp thoại chọn tệp.</summary>
    public void SetLogo(byte[] bytes, string fileName)
    {
        LogoBytes = bytes;
        LogoFileName = fileName;
    }

    [RelayCommand]
    private void ClearLogo()
    {
        LogoBytes = null;
        LogoFileName = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        SuccessMessage = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Nhập tên cơ sở";
            return;
        }

        if (!int.TryParse(BarcodeSeqDigitsText, out var seqDigits) || seqDigits <= 0)
        {
            ErrorMessage = "Số chữ số thứ tự barcode phải là số lớn hơn 0";
            return;
        }

        if (string.IsNullOrWhiteSpace(BarcodePrefix))
        {
            ErrorMessage = "Nhập tiền tố barcode";
            return;
        }

        IsBusy = true;
        try
        {
            var info = new FacilityInfo(
                Name.Trim(),
                NullIfBlank(Address),
                NullIfBlank(Phone),
                LogoBytes,
                BarcodePrefix.Trim(),
                BarcodeDatePattern.Trim(),
                seqDigits,
                BarcodeMode);

            await facility.SaveAsync(info);
            SuccessMessage = "Đã lưu thông tin cơ sở";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Lưu thông tin cơ sở thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
