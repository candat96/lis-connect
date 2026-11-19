using Lis.Core.Drivers;

namespace Lis.Connections.RawMessages;

/// <summary>
/// Gom dòng byte máy gửi thành bản tin thô theo <see cref="DriverBoundary"/> của driver. Dùng từ một luồng tại một thời điểm
/// (runner gọi dưới khoá). Thứ tự gọi quanh <c>driver.Receive</c>: <see cref="Append"/> (TRƯỚC, để Receive ném lỗi cũng không mất byte)
/// rồi <see cref="ApplyBoundaries"/> (SAU).
/// </summary>
/// <remarks>
/// Byte không thuộc lượt nào (ACK/NAK máy trả, rác, CR sau FS) dồn vào đầu bản tin kế tiếp. Xả thành bản tin <c>Partial</c> khi:
/// im lặng quá <c>idleFlush</c> kể từ byte cuối; byte đầu trong bộ đệm quá <see cref="MaxAge"/> mà driver đang rảnh
/// (máy gửi rác đều đặn); ngắt kết nối; bộ đệm chạm <see cref="MaxMessageBytes"/> (cắt ngay, phần còn lại sang bản sau,
/// để không bản tin nào bị server từ chối vì quá lớn). <c>initialSeq</c> = <c>Seq</c> lớn nhất đã dùng trước lần khởi động này, để <c>Seq</c> không lùi khi đồng hồ máy bị chỉnh lùi.
/// </remarks>
public sealed class RawMessageCollector(Guid instrumentId, TimeProvider clock, TimeSpan idleFlush, long initialSeq = 0)
{
    /// <summary>Kích thước tối đa một bản tin (byte gốc). Khớp <c>RawMessagesEndpoint.MaxMessageBytes</c> ở Lis.Server: lớn hơn bị server trả <c>khong-hop-le</c>.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Tuổi tối đa của byte rời khi driver rảnh.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly List<byte> _buffer = [];
    private readonly List<(DateTimeOffset At, int Length)> _chunks = [];
    private DateTimeOffset _lastByteAt;
    private long _lastSeq = initialSeq;
    private string _session = Guid.CreateVersion7().ToString();

    /// <summary>Số byte đang chờ cắt.</summary>
    public int Pending => _buffer.Count;

    public void NewSession() => _session = Guid.CreateVersion7().ToString();

    /// <summary>Đưa khúc byte vừa đọc vào bộ đệm. Gọi TRƯỚC <c>driver.Receive</c>.</summary>
    public void Append(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _buffer.AddRange(data);
        _chunks.Add((now, data.Length));
        _lastByteAt = now;
    }

    /// <summary>
    /// Cắt theo ranh giới của <c>driver.Receive</c>. <paramref name="dataLength"/> = độ dài khúc vừa <see cref="Append"/>
    /// (offset của ranh giới tính trong khúc đó); <paramref name="isReceiving"/> = <c>driver.IsReceiving</c> sau Receive.
    /// </summary>
    public IReadOnlyList<RawMessage> ApplyBoundaries(int dataLength, IReadOnlyList<DriverBoundary> boundaries, DateTimeOffset now, bool isReceiving)
    {
        var result = new List<RawMessage>();
        var chunkStart = _buffer.Count - dataLength;
        var consumed = 0;
        foreach (var b in boundaries)
        {
            var cut = chunkStart + b.Offset - consumed;
            cut = Math.Min(cut, _buffer.Count);
            if (cut <= 0)
            {
                continue;
            }

            Emit(result, cut, b.Abandoned, now);
            consumed += cut;
        }

        SizeFlush(result, now);
        AgeFlush(result, now, isReceiving);
        return result;
    }

    /// <summary>
    /// Cắt theo ranh giới của <c>driver.Tick</c> (offset 0 = mọi byte đã nhận), rồi xả theo thời gian im lặng / tuổi byte.
    /// </summary>
    public IReadOnlyList<RawMessage> OnTick(IReadOnlyList<DriverBoundary> boundaries, DateTimeOffset now, bool isReceiving)
    {
        var result = new List<RawMessage>();
        foreach (var b in boundaries)
        {
            if (_buffer.Count > 0)
            {
                Emit(result, _buffer.Count, b.Abandoned, now);
            }
        }

        if (_buffer.Count > 0 && now - _lastByteAt >= idleFlush)
        {
            Emit(result, _buffer.Count, true, now);
        }

        SizeFlush(result, now);
        AgeFlush(result, now, isReceiving);
        return result;
    }

    /// <summary>Kết nối đứt / dừng: còn byte thì xả một bản tin <c>Partial</c>.</summary>
    public IReadOnlyList<RawMessage> OnDisconnect(DateTimeOffset now)
    {
        var result = new List<RawMessage>();
        if (_buffer.Count > 0)
        {
            Emit(result, _buffer.Count, true, now);
        }

        return result;
    }

    private void AgeFlush(List<RawMessage> result, DateTimeOffset now, bool isReceiving)
    {
        if (!isReceiving && _buffer.Count > 0 && now - _chunks[0].At > MaxAge)
        {
            Emit(result, _buffer.Count, true, now);
        }
    }

    /// <summary>Bộ đệm chạm giới hạn thì cắt ngay thành bản tin <c>Partial</c>, phần dư (nếu có) ở lại bộ đệm cho bản sau.</summary>
    private void SizeFlush(List<RawMessage> result, DateTimeOffset now)
    {
        while (_buffer.Count >= MaxMessageBytes)
        {
            result.Add(Take(MaxMessageBytes, true, now));
        }
    }

    /// <summary>Lấy <paramref name="count"/> byte; nhiều hơn giới hạn thì chia thành các bản <c>Partial</c> ≤ 4 MB, bản cuối mang cờ <paramref name="partial"/>.</summary>
    private void Emit(List<RawMessage> result, int count, bool partial, DateTimeOffset now)
    {
        while (count > MaxMessageBytes)
        {
            result.Add(Take(MaxMessageBytes, true, now));
            count -= MaxMessageBytes;
        }

        result.Add(Take(count, partial, now));
    }

    private RawMessage Take(int count, bool partial, DateTimeOffset now)
    {
        var bytes = _buffer.GetRange(0, count).ToArray();
        _buffer.RemoveRange(0, count);
        var startedAt = _chunks[0].At;
        var left = count;
        while (left > 0)
        {
            var (at, length) = _chunks[0];
            if (length <= left)
            {
                left -= length;
                _chunks.RemoveAt(0);
            }
            else
            {
                _chunks[0] = (at, length - left);
                left = 0;
            }
        }

        _lastSeq = Math.Max(_lastSeq + 1, clock.GetUtcNow().ToUnixTimeMilliseconds() * 1000);
        return new RawMessage(Guid.CreateVersion7(), instrumentId, _lastSeq, _session, startedAt, now, bytes, partial);
    }
}
