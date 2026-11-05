namespace Lis.Core.Ingest;

/// <summary>Một kênh đo trong một bản tin máy gửi: mã kênh của máy, chuỗi giá trị thô, đơn vị và cờ máy gắn (nếu có).</summary>
public sealed record ReadingItem(string ChannelCode, string RawValue, string? Unit, string? InstrumentFlag);

/// <summary>
/// Một bản tin kết quả máy gửi lên, đã được driver (đợt 2) tách thành barcode + các kênh.
/// <see cref="RawRef"/> trỏ tới bản tin thô (định dạng do đợt 2 định nghĩa, ở đây chỉ lưu chuỗi).
/// <see cref="IsQc"/> đánh dấu mẫu nội kiểm/ngoại kiểm, không gắn với người bệnh.
/// </summary>
public sealed record InstrumentReading(
    Guid InstrumentId,
    string Barcode,
    bool IsQc,
    DateTimeOffset ReceivedAt,
    string? RawRef,
    IReadOnlyList<ReadingItem> Items);
