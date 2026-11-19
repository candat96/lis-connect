namespace Lis.Connections.RawMessages;

/// <summary>
/// Một bản tin thô: khúc byte máy gửi, cắt theo ranh giới driver. <see cref="Id"/> là UUID v7 do agent sinh (server chống trùng theo đây);
/// <see cref="Seq"/> tăng dần theo từng máy; <see cref="Session"/> đổi mỗi lần mở kết nối; <see cref="Partial"/> = bản tin dở dang.
/// </summary>
public sealed record RawMessage(
    Guid Id,
    Guid InstrumentId,
    long Seq,
    string Session,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    byte[] Bytes,
    bool Partial);

/// <summary>Nơi nhận bản tin thô. <see cref="SaveAsync"/> chỉ trả về khi bản tin đã nằm chắc trên đĩa.</summary>
public interface IRawMessageSink
{
    Task SaveAsync(RawMessage message, CancellationToken ct);

    /// <summary><c>Seq</c> lớn nhất đã ghi cho máy này (0 nếu chưa có). Dùng khởi tạo bộ gom sau khi khởi động lại.</summary>
    long MaxSeq(Guid instrumentId) => 0;
}
