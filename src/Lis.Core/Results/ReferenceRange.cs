namespace Lis.Core.Results;

/// <summary>
/// Một khoảng tham chiếu của một chỉ số xét nghiệm, có thể giới hạn theo
/// giới tính, độ tuổi (theo ngày) và máy xét nghiệm. <see cref="Sex"/> null
/// nghĩa là khoảng chung cho cả hai giới. <see cref="AgeFromDays"/>/
/// <see cref="AgeToDays"/> null nghĩa là mở về phía đó (không giới hạn).
/// <see cref="TextRange"/> dùng cho khoảng hiển thị dạng chữ (không so ngưỡng được).
/// </summary>
public sealed record ReferenceRange(
    Guid Id,
    Guid ServiceId,
    char? Sex,
    int? AgeFromDays,
    int? AgeToDays,
    Guid? InstrumentId,
    decimal? Low,
    decimal? High,
    decimal? CriticalLow,
    decimal? CriticalHigh,
    string? TextRange);
