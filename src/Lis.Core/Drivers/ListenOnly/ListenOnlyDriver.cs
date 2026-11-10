namespace Lis.Core.Drivers.ListenOnly;

/// <summary>
/// Driver "Chỉ nghe" (họ <c>chi-nghe</c>, thiết kế mục 9): mở cổng, để lớp chạy kết nối ghi byte thô
/// vào log, không giải mã, không ACK, không trả lời gì. Dùng khi chưa biết máy nói giao thức gì —
/// xem log thô (và gợi ý giao thức) rồi đổi sang họ đúng.
/// </summary>
/// <remarks>
/// Máy chờ ACK sẽ gửi lại hoặc báo lỗi truyền — đó là điều mong muốn ở chế độ này: chỉ thu mẫu bản
/// tin, không nhận kết quả.
/// </remarks>
public sealed class ListenOnlyDriver : IInstrumentDriver
{
    public const string FamilyCode = "chi-nghe";

    public string Family => FamilyCode;

    public DriverOutput Receive(ReadOnlySpan<byte> data, DateTimeOffset now) => new();

    public DriverOutput Tick(DateTimeOffset now) => new();

    public DriverOutput AnswerQuery(DriverQuery query, IReadOnlyList<string> channelCodes, DateTimeOffset now) => new();

    public void Reset()
    {
    }
}
