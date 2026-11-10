namespace Lis.Simulator.Links;

/// <summary>
/// Đường nối byte giữa bộ giả lập (đóng vai máy) và LIS: TCP, cổng COM, hoặc bộ nhớ (khi test).
/// </summary>
public interface IMachineLink : IAsyncDisposable
{
    /// <summary>Gửi một khối byte sang LIS.</summary>
    Task WriteAsync(byte[] data, CancellationToken ct);

    /// <summary>
    /// Đọc các byte LIS gửi về. Chờ tối đa <paramref name="timeout"/> cho byte đầu tiên, rồi gom
    /// thêm những byte đến sát ngay sau đó. Trả mảng rỗng nếu hết giờ hoặc kết nối đã đóng.
    /// </summary>
    Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken ct);
}
