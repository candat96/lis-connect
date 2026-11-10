namespace Lis.Simulator.Links;

/// <summary>
/// Đường nối trên một <see cref="Stream"/> (NetworkStream của TCP hoặc BaseStream của cổng COM).
/// Không hủy lệnh đọc đang chờ khi hết giờ (hủy giữa chừng có thể làm mất byte): giữ lại lệnh đọc
/// đó cho lần <see cref="ReadAsync"/> sau.
/// </summary>
public class StreamLink(Stream stream, IDisposable? owner = null) : IMachineLink
{
    private static readonly TimeSpan QuietGap = TimeSpan.FromMilliseconds(40);

    private readonly byte[] _buffer = new byte[4096];
    private Task<int>? _pending;

    /// <summary>Đầu bên kia đã đóng kết nối.</summary>
    public bool IsClosed { get; private set; }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var result = new List<byte>();
        var wait = timeout;
        while (!IsClosed)
        {
            _pending ??= stream.ReadAsync(_buffer, CancellationToken.None).AsTask();
            var finished = await Task.WhenAny(_pending, Task.Delay(wait, ct));
            ct.ThrowIfCancellationRequested();
            if (finished != _pending)
            {
                break;
            }

            var count = await _pending;
            _pending = null;
            if (count == 0)
            {
                IsClosed = true;
                break;
            }

            result.AddRange(_buffer.AsSpan(0, count).ToArray());
            wait = QuietGap;
        }

        return [.. result];
    }

    public virtual ValueTask DisposeAsync()
    {
        stream.Dispose();
        owner?.Dispose();
        return ValueTask.CompletedTask;
    }
}
