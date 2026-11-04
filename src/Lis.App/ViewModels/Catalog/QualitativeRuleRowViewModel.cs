using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Results;

namespace Lis.App.ViewModels.Catalog;

/// <summary>Nhãn tiếng Việt có dấu cho <see cref="ResultFlag"/>, dùng trên ComboBox của màn Danh mục.</summary>
public static class ResultFlagExtensions
{
    public static string Label(this ResultFlag flag) => flag switch
    {
        ResultFlag.None => "(không ghi đè)",
        ResultFlag.Low => "Thấp",
        ResultFlag.High => "Cao",
        ResultFlag.CriticalLow => "Nguy hiểm thấp",
        ResultFlag.CriticalHigh => "Nguy hiểm cao",
        ResultFlag.Abnormal => "Bất thường",
        _ => flag.ToString(),
    };
}

/// <summary>
/// Một dòng trong tab "Quy đổi định tính". <see cref="Flag"/> dùng <see cref="ResultFlag.None"/>
/// để thể hiện "không ghi đè cờ" (quy đổi về <c>null</c> khi lưu CSDL) — đơn giản hơn so với
/// chọn giữa null và các giá trị enum trên ComboBox. Lệnh "Xóa" gọi ngược lại
/// <see cref="CatalogViewModel"/> qua callback (xem <see cref="ChildServiceViewModel"/>).
/// </summary>
public partial class QualitativeRuleRowViewModel : ObservableObject
{
    public QualitativeRuleRowViewModel(Action<QualitativeRuleRowViewModel> remove)
    {
        _remove = remove;
    }

    private readonly Action<QualitativeRuleRowViewModel> _remove;

    /// <summary>Id máy áp dụng riêng, để trống = áp dụng cho mọi máy.</summary>
    [ObservableProperty]
    private string? _instrumentIdText;

    [ObservableProperty]
    private string _rawValue = "";

    [ObservableProperty]
    private string _displayValue = "";

    [ObservableProperty]
    private ResultFlag _flag = ResultFlag.None;

    public static readonly IReadOnlyList<ResultFlag> FlagOptions = Enum.GetValues<ResultFlag>();

    [RelayCommand]
    private void Remove() => _remove(this);

    public static QualitativeRuleRowViewModel From(QualitativeRule rule, Action<QualitativeRuleRowViewModel> remove) => new(remove)
    {
        InstrumentIdText = rule.InstrumentId?.ToString(),
        RawValue = rule.RawValue,
        DisplayValue = rule.DisplayValue,
        Flag = rule.Flag ?? ResultFlag.None,
    };

    public QualitativeRule ToRule(Guid serviceId) => new(
        Guid.TryParse(InstrumentIdText, out var instrumentId) ? instrumentId : null,
        serviceId,
        RawValue,
        DisplayValue,
        Flag == ResultFlag.None ? null : Flag);
}
