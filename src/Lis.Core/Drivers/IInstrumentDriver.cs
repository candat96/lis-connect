using Lis.Core.Ingest;

namespace Lis.Core.Drivers;

/// <summary>Một bản tin kết quả driver đã tách xong: barcode, cờ QC, các kênh và văn bản thô của bản tin (để lưu raw_ref).</summary>
public sealed record DriverReading(string Barcode, bool IsQc, IReadOnlyList<ReadingItem> Items, string RawText);

/// <summary>
/// Máy hỏi chỉ định (host query) cho một mẫu. Lớp chạy kết nối tra CSDL rồi gọi
/// <see cref="IInstrumentDriver.AnswerQuery"/> với cùng <see cref="Token"/>.
/// <see cref="Context"/> là dữ liệu riêng của driver (vd số rack/cup) để dựng câu trả lời.
/// </summary>
public sealed record DriverQuery(string Barcode, object Token, object? Context = null);

/// <summary>Đầu ra của một lần gọi driver: các khối byte cần gửi trả máy (theo thứ tự), bản tin kết quả, câu hỏi chỉ định, ghi chú cho log thô.</summary>
public sealed class DriverOutput
{
    public List<byte[]> Send { get; } = [];

    public List<DriverReading> Readings { get; } = [];

    public List<DriverQuery> Queries { get; } = [];

    /// <summary>Sự kiện đáng ghi vào log thô (vd "Checksum sai khung 3 — đã NAK").</summary>
    public List<string> Notes { get; } = [];

    public bool IsEmpty => Send.Count == 0 && Readings.Count == 0 && Queries.Count == 0 && Notes.Count == 0;
}

/// <summary>
/// Bộ giải mã giao thức của một họ máy — thuần logic, không đụng cổng COM/socket/CSDL: lớp chạy
/// kết nối đưa byte nhận được vào <see cref="Receive"/>, gửi các khối trong
/// <see cref="DriverOutput.Send"/> ra máy, chuyển <see cref="DriverOutput.Readings"/> cho
/// IngestService. Một thể hiện phục vụ đúng một kết nối; không an toàn đa luồng (lớp chạy gọi
/// tuần tự).
/// </summary>
public interface IInstrumentDriver
{
    /// <summary>Họ giao thức, trùng giá trị <c>instrument.protocol_family</c> (vd "astm-e1394").</summary>
    string Family { get; }

    /// <summary>Byte mới nhận từ máy (có thể là nửa khung, nhiều khung, rác giữa các khung).</summary>
    DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now);

    /// <summary>Gọi định kỳ (~1 giây) để xử lý hết giờ chờ của giao thức (vd bỏ phiên ASTM treo).</summary>
    DriverOutput Tick(DateTimeOffset now);

    /// <summary>
    /// Trả lời câu hỏi chỉ định <paramref name="query"/> với danh sách mã kênh cần chạy (rỗng =
    /// không có chỉ định). Driver không hỗ trợ hai chiều thì trả về đầu ra rỗng.
    /// </summary>
    DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now);

    /// <summary>Kết nối vừa mở lại/đứt: bỏ mọi trạng thái nửa chừng.</summary>
    void Reset();
}
