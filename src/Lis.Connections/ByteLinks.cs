using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Lis.Core.Instruments;

namespace Lis.Connections;

/// <summary>
/// Một đường truyền byte hai chiều tới máy xét nghiệm (COM hoặc TCP). <see cref="OpenAsync"/> chờ
/// tới khi thông (TCP server: chờ máy nối vào); <see cref="ReadAsync"/> trả 0 khi đứt.
/// </summary>
public interface IByteLink : IAsyncDisposable
{
    /// <summary>Mô tả ngắn cho log/trạng thái, vd "COM3 · 9600 8N1", "TCP chờ ở cổng 5100".</summary>
    string Description { get; }

    Task OpenAsync(CancellationToken ct);

    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>Tạo đường truyền theo <see cref="ConnectionSettings"/> (cấu hình ở màn Máy &amp; map).</summary>
public static class ByteLinkFactory
{
    public static IByteLink Create(ConnectionSettings settings) => settings.Kind switch
    {
        ConnectionKind.Serial => new SerialLink(settings),
        ConnectionKind.TcpServer => new TcpServerLink(settings.TcpPort ?? throw new InvalidOperationException("Chưa khai cổng TCP")),
        ConnectionKind.TcpClient => new TcpClientLink(
            settings.Host ?? throw new InvalidOperationException("Chưa khai địa chỉ máy"),
            settings.TcpPort ?? throw new InvalidOperationException("Chưa khai cổng TCP")),
        _ => throw new InvalidOperationException("Máy chưa cấu hình kết nối"),
    };
}

/// <summary>Cổng COM (RS-232 / USB-serial). Đọc bằng BaseStream để không phụ thuộc sự kiện DataReceived.</summary>
public sealed class SerialLink(ConnectionSettings settings) : IByteLink
{
    private SerialPort? _port;

    public string Description => settings.Describe();

    public Task OpenAsync(CancellationToken ct)
    {
        var port = new SerialPort(settings.PortName!, settings.BaudRate, Enum.Parse<Parity>(settings.Parity, ignoreCase: true), settings.DataBits,
            settings.StopBits switch { 2 => StopBits.Two, 15 => StopBits.OnePointFive, _ => StopBits.One })
        {
            DtrEnable = true,
            RtsEnable = true,
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            port.Dispose();
            throw new IOException($"{settings.PortName} đang bị chương trình khác giữ (LIS cũ, phần mềm hãng, màn dò cổng…)");
        }

        _port = port;
        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        await (_port ?? throw new InvalidOperationException("Cổng chưa mở")).BaseStream.ReadAsync(buffer, ct);

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var stream = (_port ?? throw new InvalidOperationException("Cổng chưa mở")).BaseStream;
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _port?.Close();
        }
        catch (IOException)
        {
            // Cáp USB-serial rút ra giữa chừng: đóng cổng có thể ném — bỏ qua, đằng nào cũng giải phóng.
        }

        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>LIS nối tới máy (máy là TCP server).</summary>
public sealed class TcpClientLink(string host, int port) : IByteLink
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public string Description => $"TCP nối tới {host}:{port}";

    public async Task OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        await (_stream ?? throw new InvalidOperationException("Chưa nối")).ReadAsync(buffer, ct);

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        await (_stream ?? throw new InvalidOperationException("Chưa nối")).WriteAsync(data, ct);

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// LIS chờ máy nối vào (máy là TCP client — mặc định của Mindray BC-5380). Mỗi lần mở nhận đúng
/// một kết nối; máy nối lại thì vòng chạy gọi mở lại.
/// </summary>
public sealed class TcpServerLink(int port) : IByteLink
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public string Description => _client?.Client.RemoteEndPoint is { } remote ? $"TCP cổng {port} ← {remote}" : $"TCP chờ máy nối vào cổng {port}";

    public async Task OpenAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _listener.Start(1);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _listener = null;
            throw new IOException($"Cổng TCP {port} đang bị chương trình khác dùng");
        }

        _client = await _listener.AcceptTcpClientAsync(ct);
        _client.NoDelay = true;
        _stream = _client.GetStream();

        // Chỉ nhận một máy: ngừng nghe để máy thứ hai (hoặc lần nối trùng) bị từ chối thay vì treo.
        _listener.Stop();
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        await (_stream ?? throw new InvalidOperationException("Chưa có máy nối vào")).ReadAsync(buffer, ct);

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        await (_stream ?? throw new InvalidOperationException("Chưa có máy nối vào")).WriteAsync(data, ct);

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        _stream = null;
        _client = null;
        _listener = null;
        return ValueTask.CompletedTask;
    }
}
