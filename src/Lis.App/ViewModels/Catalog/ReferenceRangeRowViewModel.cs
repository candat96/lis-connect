using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// Một dòng trong tab "Khoảng tham chiếu". Tuổi nhập theo ngày/tháng/năm (xem
/// <see cref="AgeUnitExtensions.ToDays"/>) nhưng lưu CSDL theo ngày, đúng với
/// <see cref="ReferenceRange.AgeFromDays"/>/<see cref="ReferenceRange.AgeToDays"/>. Lệnh
/// "Xóa" gọi ngược lại <see cref="CatalogViewModel"/> qua callback (xem
/// <see cref="ChildServiceViewModel"/>).
/// </summary>
public partial class ReferenceRangeRowViewModel : ObservableObject
{
    public ReferenceRangeRowViewModel(Action<ReferenceRangeRowViewModel> remove)
    {
        _remove = remove;
    }

    private readonly Action<ReferenceRangeRowViewModel> _remove;

    public Guid Id { get; private init; }

    /// <summary>null = chung cho cả hai giới. "M" = nam, "F" = nữ.</summary>
    [ObservableProperty]
    private string? _sex;

    [ObservableProperty]
    private int? _ageFromValue;

    [ObservableProperty]
    private AgeUnit _ageFromUnit = AgeUnit.Nam;

    [ObservableProperty]
    private int? _ageToValue;

    [ObservableProperty]
    private AgeUnit _ageToUnit = AgeUnit.Nam;

    /// <summary>Id máy áp dụng riêng, để trống = áp dụng cho mọi máy.</summary>
    [ObservableProperty]
    private string? _instrumentIdText;

    [ObservableProperty]
    private decimal? _low;

    [ObservableProperty]
    private decimal? _high;

    [ObservableProperty]
    private decimal? _criticalLow;

    [ObservableProperty]
    private decimal? _criticalHigh;

    [ObservableProperty]
    private string? _textRange;

    [RelayCommand]
    private void Remove() => _remove(this);

    public static ReferenceRangeRowViewModel From(ReferenceRange range, Action<ReferenceRangeRowViewModel> remove)
    {
        var row = new ReferenceRangeRowViewModel(remove)
        {
            Id = range.Id,
            Sex = range.Sex?.ToString(),
            InstrumentIdText = range.InstrumentId?.ToString(),
            Low = range.Low,
            High = range.High,
            CriticalLow = range.CriticalLow,
            CriticalHigh = range.CriticalHigh,
            TextRange = range.TextRange,
        };

        if (range.AgeFromDays is { } fromDays)
        {
            (row.AgeFromValue, row.AgeFromUnit) = AgeUnitExtensions.FromDays(fromDays);
        }

        if (range.AgeToDays is { } toDays)
        {
            (row.AgeToValue, row.AgeToUnit) = AgeUnitExtensions.FromDays(toDays);
        }

        return row;
    }

    public ReferenceRange ToReferenceRange(Guid serviceId) => new(
        Id,
        serviceId,
        string.IsNullOrWhiteSpace(Sex) ? null : Sex[0],
        AgeFromValue is { } fromValue ? AgeFromUnit.ToDays(fromValue) : null,
        AgeToValue is { } toValue ? AgeToUnit.ToDays(toValue) : null,
        Guid.TryParse(InstrumentIdText, out var instrumentId) ? instrumentId : null,
        Low,
        High,
        CriticalLow,
        CriticalHigh,
        string.IsNullOrWhiteSpace(TextRange) ? null : TextRange);
}
