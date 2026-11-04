using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lis.Core.Results;
using Lis.Data.Results;

namespace Lis.App.ViewModels.Results;

/// <summary>
/// Một dòng trên bảng chỉ số của màn Kết quả. <see cref="EditText"/> là nội dung ô giá trị
/// đang gõ; <see cref="IsDirty"/> khi khác giá trị đã lưu. Các lệnh ủy quyền về
/// <see cref="ResultsViewModel"/> (chủ sở hữu) — dòng chỉ giữ dữ liệu hiển thị.
/// </summary>
public partial class ResultRowViewModel : ObservableObject
{
    /// <summary>Độ thụt (px) của mỗi mức con.</summary>
    private const double IndentPerLevel = 20;

    private readonly ResultsViewModel _owner;

    public ResultRowViewModel(ResultRow row, bool isEditable, ResultsViewModel owner)
    {
        _owner = owner;
        _row = row;
        _isEditable = isEditable && !row.IsPanelHeader;
        _editText = row.Value ?? "";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Code))]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(IsPanelHeader))]
    [NotifyPropertyChangedFor(nameof(IndentWidth))]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(Unit))]
    [NotifyPropertyChangedFor(nameof(RefText))]
    [NotifyPropertyChangedFor(nameof(FlagText))]
    [NotifyPropertyChangedFor(nameof(IsHigh))]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    [NotifyPropertyChangedFor(nameof(IsCritical))]
    [NotifyPropertyChangedFor(nameof(IsAbnormal))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(PendingValue))]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private ResultRow _row;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _editText;

    [ObservableProperty]
    private bool _isEditable;

    public Guid OrderItemId => Row.OrderItemId;

    public Guid? ResultId => Row.ResultId;

    public string Code => Row.ServiceCode;

    public string Name => Row.ServiceName;

    public bool IsPanelHeader => Row.IsPanelHeader;

    public double IndentWidth => Row.Depth * IndentPerLevel;

    public string? Value => Row.Value;

    public bool HasValue => !string.IsNullOrEmpty(Row.Value);

    public string? Unit => Row.Unit;

    public string? RefText => Row.RefText;

    public bool IsDirty => EditText != (Row.Value ?? "");

    public string FlagText => Row.Flag switch
    {
        ResultFlag.High => "↑ Cao",
        ResultFlag.Low => "↓ Thấp",
        ResultFlag.CriticalHigh or ResultFlag.CriticalLow => "‼ Nguy hiểm",
        ResultFlag.Abnormal => "Bất thường",
        _ => "",
    };

    public bool IsHigh => Row.Flag == ResultFlag.High;

    public bool IsLow => Row.Flag == ResultFlag.Low;

    public bool IsCritical => Row.Flag is ResultFlag.CriticalHigh or ResultFlag.CriticalLow;

    public bool IsAbnormal => Row.Flag == ResultFlag.Abnormal;

    public string StatusText => Row.Status == SampleStatus.Approved ? "✓ Đã duyệt" : "";

    public string? PendingValue => Row.PendingValue;

    public bool HasPending => Row.PendingValue is not null;

    /// <summary>Thay dữ liệu dòng sau khi nạp lại mẫu — giữ nguyên đối tượng để ô đang có focus không bị dựng lại.</summary>
    public void Update(ResultRow row, bool isEditable)
    {
        Row = row;
        IsEditable = isEditable && !row.IsPanelHeader;
        EditText = row.Value ?? "";
    }

    partial void OnEditTextChanged(string value) => _owner.OnRowEditTextChanged();

    [RelayCommand]
    private Task CommitAsync() => _owner.CommitValueAsync(this);

    [RelayCommand]
    private void Cancel() => _owner.CancelEdit(this);

    [RelayCommand]
    private Task AcceptPendingAsync() => _owner.AcceptPendingAsync(this);

    [RelayCommand]
    private Task RejectPendingAsync() => _owner.RejectPendingAsync(this);
}
