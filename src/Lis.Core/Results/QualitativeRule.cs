namespace Lis.Core.Results;

/// <summary>
/// Luật quy đổi một giá trị định tính thô (ví dụ "pos"/"neg" máy gửi) sang
/// chuỗi hiển thị và cờ ghi đè. <see cref="InstrumentId"/> null nghĩa là luật
/// chung cho mọi máy của chỉ số này.
/// </summary>
public sealed record QualitativeRule(
    Guid? InstrumentId,
    Guid ServiceId,
    string RawValue,
    string DisplayValue,
    ResultFlag? Flag);
