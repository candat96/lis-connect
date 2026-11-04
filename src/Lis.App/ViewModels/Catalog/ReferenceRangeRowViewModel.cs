using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core;
using Lis.Core.Results;

namespace Lis.App.ViewModels.Catalog;

/// <summary>Đơn vị nhập tuổi trên form khoảng tham chiếu — CSDL luôn lưu quy ra ngày.</summary>
public enum AgeUnit
{
    Ngay,
    Thang,
    Nam,
}

/// <summary>Quy đổi tuổi nhập theo ngày/tháng/năm sang số ngày để lưu CSDL.</summary>
public static class AgeUnitExtensions
{
    public static readonly IReadOnlyList<AgeUnit> All = [AgeUnit.Ngay, AgeUnit.Thang, AgeUnit.Nam];

    public static string Label(this AgeUnit unit) => unit switch
    {
        AgeUnit.Ngay => "Ngày",
        AgeUnit.Thang => "Tháng",
        AgeUnit.Nam => "Năm",
        _ => unit.ToString(),
    };

    /// <summary>
    /// Quy đổi sang ngày: 1 tháng = 30 ngày, 1 năm = 365 ngày (quy ước đơn giản để lưu khoảng
    /// tham chiếu theo ngày — không dùng để tính tuổi chính xác của người bệnh).
    /// </summary>
    public static int ToDays(this AgeUnit unit, int value) => unit switch
    {
        AgeUnit.Ngay => value,
        AgeUnit.Thang => value * 30,
        AgeUnit.Nam => value * 365,
        _ => value,
    };

    /// <summary>Chọn đơn vị hiển thị đẹp nhất cho một số ngày đã lưu (ưu tiên năm, rồi tháng, rồi ngày).</summary>
    public static (int Value, AgeUnit Unit) FromDays(int days)
    {
        if (days != 0 && days % 365 == 0)
        {
            return (days / 365, AgeUnit.Nam);
        }

        if (days != 0 && days % 30 == 0)
        {
            return (days / 30, AgeUnit.Thang);
        }

        return (days, AgeUnit.Ngay);
    }
}

/// <summary>
/// Một dòng trong tab "Khoảng tham chiếu". Mọi ô số (tuổi, thấp/cao/nguy hiểm thấp/nguy hiểm
/// cao) là ô chữ (<c>string?</c>) chứ không dùng <c>NumericUpDown</c>: như vậy nhập sai (vd
/// "1.2.3", chữ) không bị lặng lẽ quy thành rỗng — <see cref="ToReferenceRange"/> parse từng ô,
/// ô không parse được thì thêm lỗi vào <paramref name="errors"/> thay vì bỏ qua. Tuổi nhập
/// theo ngày/tháng/năm (xem <see cref="AgeUnitExtensions.ToDays"/>) nhưng lưu CSDL theo ngày.
/// </summary>
public partial class ReferenceRangeRowViewModel : ObservableObject
{
    public ReferenceRangeRowViewModel(ObservableCollection<InstrumentItem> instruments, Action<ReferenceRangeRowViewModel> remove)
    {
        Instruments = instruments;
        _remove = remove;
    }

    private readonly Action<ReferenceRangeRowViewModel> _remove;

    public Guid Id { get; private init; }

    /// <summary>Danh sách máy để ComboBox của chính dòng này hiển thị — tham chiếu dùng chung từ <c>RangesEditorViewModel</c>.</summary>
    public ObservableCollection<InstrumentItem> Instruments { get; }

    /// <summary>null = chung cho cả hai giới. "M" = nam, "F" = nữ.</summary>
    [ObservableProperty]
    private string? _sex;

    [ObservableProperty]
    private string? _ageFromText;

    [ObservableProperty]
    private AgeUnit _ageFromUnit = AgeUnit.Nam;

    [ObservableProperty]
    private string? _ageToText;

    [ObservableProperty]
    private AgeUnit _ageToUnit = AgeUnit.Nam;

    [ObservableProperty]
    private InstrumentItem _instrument = InstrumentItem.Any;

    [ObservableProperty]
    private string? _lowText;

    [ObservableProperty]
    private string? _highText;

    [ObservableProperty]
    private string? _criticalLowText;

    [ObservableProperty]
    private string? _criticalHighText;

    [ObservableProperty]
    private string? _textRange;

    [RelayCommand]
    private void Remove() => _remove(this);

    public static ReferenceRangeRowViewModel From(ReferenceRange range, ObservableCollection<InstrumentItem> instruments, Action<ReferenceRangeRowViewModel> remove)
    {
        var row = new ReferenceRangeRowViewModel(instruments, remove)
        {
            Id = range.Id,
            Sex = range.Sex?.ToString(),
            Instrument = range.InstrumentId is { } instrumentId
                ? instruments.FirstOrDefault(i => i.Id == instrumentId) ?? InstrumentItem.Any
                : InstrumentItem.Any,
            LowText = range.Low?.ToString(CultureInfo.InvariantCulture),
            HighText = range.High?.ToString(CultureInfo.InvariantCulture),
            CriticalLowText = range.CriticalLow?.ToString(CultureInfo.InvariantCulture),
            CriticalHighText = range.CriticalHigh?.ToString(CultureInfo.InvariantCulture),
            TextRange = range.TextRange,
        };

        if (range.AgeFromDays is { } fromDays)
        {
            var (value, unit) = AgeUnitExtensions.FromDays(fromDays);
            row.AgeFromText = value.ToString(CultureInfo.InvariantCulture);
            row.AgeFromUnit = unit;
        }

        if (range.AgeToDays is { } toDays)
        {
            var (value, unit) = AgeUnitExtensions.FromDays(toDays);
            row.AgeToText = value.ToString(CultureInfo.InvariantCulture);
            row.AgeToUnit = unit;
        }

        return row;
    }

    /// <summary>
    /// Chuyển dòng thành <see cref="ReferenceRange"/>. Ô số có nội dung nhưng không parse được
    /// thì thêm "Dòng {rowNumber}: {tên ô} không phải là số" vào <paramref name="errors"/> (ô
    /// bỏ trống thì không lỗi — nghĩa là không giới hạn).
    /// </summary>
    public ReferenceRange ToReferenceRange(Guid serviceId, int rowNumber, List<string> errors) => new(
        Id,
        serviceId,
        string.IsNullOrWhiteSpace(Sex) ? null : Sex[0],
        ParseInt(AgeFromText, rowNumber, "tuổi từ", errors) is { } ageFromValue ? AgeFromUnit.ToDays(ageFromValue) : null,
        ParseInt(AgeToText, rowNumber, "tuổi đến", errors) is { } ageToValue ? AgeToUnit.ToDays(ageToValue) : null,
        Instrument.Id == Guid.Empty ? null : Instrument.Id,
        ParseDecimal(LowText, rowNumber, "giá trị thấp", errors),
        ParseDecimal(HighText, rowNumber, "giá trị cao", errors),
        ParseDecimal(CriticalLowText, rowNumber, "nguy hiểm thấp", errors),
        ParseDecimal(CriticalHighText, rowNumber, "nguy hiểm cao", errors),
        string.IsNullOrWhiteSpace(TextRange) ? null : TextRange);

    /// <summary>
    /// Parse ô số thập phân qua <see cref="DecimalInput"/> — chấp nhận dấu phẩy hoặc dấu chấm
    /// làm dấu thập phân, không đoán ý dấu phân cách hàng nghìn (vd "130,5" hay "130.5" đều
    /// thành 130.5; "1.234,5" là sai, không lặng lẽ thành 1234.5).
    /// </summary>
    private static decimal? ParseDecimal(string? text, int rowNumber, string fieldLabel, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DecimalInput.TryParse(text, out var value))
        {
            return value;
        }

        errors.Add($"Dòng {rowNumber}: {fieldLabel} không phải là số");
        return null;
    }

    private static int? ParseInt(string? text, int rowNumber, string fieldLabel, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add($"Dòng {rowNumber}: {fieldLabel} không phải là số");
        return null;
    }
}
