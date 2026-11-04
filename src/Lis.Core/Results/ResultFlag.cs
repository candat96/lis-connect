namespace Lis.Core.Results;

/// <summary>
/// Cờ cảnh báo của một kết quả xét nghiệm. <see cref="Abnormal"/> chỉ được
/// sinh ra qua quy đổi định tính (<see cref="QualitativeMapper"/>), không bao
/// giờ do so sánh số với khoảng tham chiếu.
/// </summary>
public enum ResultFlag
{
    None,
    Low,
    High,
    CriticalLow,
    CriticalHigh,
    Abnormal,
}
