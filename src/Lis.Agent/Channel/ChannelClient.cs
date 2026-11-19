using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lis.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>
/// Giữ kênh lệnh WebSocket tới server: nối bằng khoá agent (chỉ ở header <c>X-Agent-Key</c> — không bao giờ ở URL hay log),
/// gửi <c>hello</c>, nhận lệnh giao cho <see cref="CommandDispatcher"/>, báo <c>config-changed</c> qua <see cref="ConfigChanged"/>.
/// Đứt thì nối lại 1, 2, 4, 8, 16, 30, 60 giây (tối đa 60, ±20 %); nối được thì về lại 1 giây.
/// Kênh hỏng chỉ ghi cảnh báo — không bao giờ ném ra ngoài và không đụng tới kết nối máy xét nghiệm.
/// </summary>
public sealed class ChannelClient
{
    public const string ChannelPath = "agent/v1/channel";
    private static readonly double[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Uri _channelUrl;
    private readonly string _agentKey;
    private readonly CommandDispatcher _dispatcher;
    private readonly AgentPaths _paths;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Func<Uri, string, CancellationToken, Task<WebSocket>> _connect;
    private readonly Action<string>? _note;
    private bool _reportedDown;

    public ChannelClient(
        Uri serverUrl, string agentKey, CommandDispatcher dispatcher, AgentPaths paths, TimeProvider clock, ILogger logger,
        Func<Uri, string, CancellationToken, Task<WebSocket>>? connect = null, Action<string>? note = null)
    {
        _channelUrl = ChannelUrl(serverUrl);
        _agentKey = agentKey;
        _dispatcher = dispatcher;
        _paths = paths;
        _clock = clock;
        _logger = logger;
        _connect = connect ?? ConnectRealAsync;
        _note = note;
    }

    /// <summary>Server báo web vừa sửa / xoá / tạo máy của agent (có thể gọi từ luồng nền).</summary>
    public event Action? ConfigChanged;

    /// <summary><c>http→ws</c>, <c>https→wss</c>, đường <c>agent/v1/channel</c> (giữ tiền tố đường dẫn nếu server đặt sau thư mục con).</summary>
    public static Uri ChannelUrl(Uri serverUrl)
    {
        var secure = serverUrl.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || serverUrl.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
        var builder = new UriBuilder(serverUrl)
        {
            Scheme = secure ? "wss" : "ws",
            Path = serverUrl.AbsolutePath.TrimEnd('/') + "/" + ChannelPath,
            Query = "",
            Fragment = "",
        };
        if (serverUrl.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    /// <summary>Thời gian chờ trước lần nối thứ <paramref name="attempt"/> (từ 0); <paramref name="jitter01"/> trong [0,1] → hệ số 0,8…1,2.</summary>
    public static TimeSpan BackoffFor(int attempt, double jitter01)
    {
        var baseSeconds = BackoffSeconds[Math.Clamp(attempt, 0, BackoffSeconds.Length - 1)];
        return TimeSpan.FromSeconds(baseSeconds * (0.8 + (0.4 * Math.Clamp(jitter01, 0, 1))));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            WebSocket? socket = null;
            try
            {
                socket = await _connect(_channelUrl, _agentKey, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ReportDown($"Chưa nối được kênh lệnh tới server: {ex.Message}");
            }

            if (socket is not null)
            {
                try
                {
                    await SessionAsync(socket, ct, () =>
                    {
                        attempt = 0; // chỉ coi là nối được khi hello đã gửi xong
                        _reportedDown = false;
                    });
                    ReportDown("Kênh lệnh tới server đã đóng — đang nối lại");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ReportDown($"Kênh lệnh tới server đã đứt: {ex.Message}");
                }
                finally
                {
                    socket.Dispose();
                }
            }

            try
            {
                await Task.Delay(BackoffFor(attempt++, Random.Shared.NextDouble()), _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ReportDown(string message)
    {
        _logger.LogWarning("{Message}", message);
        if (!_reportedDown)
        {
            _reportedDown = true;
            _note?.Invoke(message); // chỉ báo lần đầu mỗi đợt mất kênh — không dội log mỗi lần thử lại
        }
    }

    private async Task SessionAsync(WebSocket socket, CancellationToken ct, Action onHelloSent)
    {
        using var sendLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(ChannelEnvelope envelope)
        {
            var bytes = Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(envelope));
            await sendLock.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    throw new WebSocketException("Kênh đã đóng");
                }

                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                sendLock.Release();
            }
        }

        try
        {
            await HelloAsync(SendAsync);
            onHelloSent();
            _logger.LogInformation("Đã nối kênh lệnh tới server");

            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                message.Write(buffer, 0, received.Count);
                if (message.Length > ChannelProtocol.MaxMessageBytes)
                {
                    return; // khung quá lớn: bỏ kênh, nối lại
                }

                if (received.EndOfMessage)
                {
                    var envelope = ChannelProtocol.Parse(message.ToArray());
                    message.SetLength(0);
                    if (envelope is not null)
                    {
                        Handle(envelope, SendAsync, ct);
                    }
                }
            }
        }
        finally
        {
            await CloseQuietlyAsync(socket);
        }
    }

    /// <summary>Gửi <c>hello</c> (kèm kết quả cập nhật còn lại, xoá tệp sau khi gửi), rồi ghi dấu đã nối.</summary>
    private async Task HelloAsync(Func<ChannelEnvelope, Task> send)
    {
        var update = ReadUpdateReport();
        await send(new ChannelEnvelope(
            ChannelProtocol.Hello, Version: AgentVersion.Current, MachineName: Environment.MachineName, Update: update));
        if (update is not null)
        {
            TryDelete(_paths.UpdateReport);
        }

        try
        {
            Directory.CreateDirectory(_paths.Root);
            File.WriteAllText(
                _paths.ConnectedMarker,
                JsonSerializer.Serialize(new { version = AgentVersion.Current, at = _clock.GetUtcNow() }, Json),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không ghi được dấu đã nối kênh: {Message}", ex.Message);
        }
    }

    private UpdateReport? ReadUpdateReport()
    {
        try
        {
            return File.Exists(_paths.UpdateReport)
                ? JsonSerializer.Deserialize<UpdateReport>(File.ReadAllText(_paths.UpdateReport), Json)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không đọc được kết quả cập nhật: {Message}", ex.Message);
            return null;
        }
    }

    private void Handle(ChannelEnvelope envelope, Func<ChannelEnvelope, Task> send, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case ChannelProtocol.Command when envelope.Id is not null:
                _ = RunCommandAsync(envelope, send, ct);
                break;
            case ChannelProtocol.ConfigChanged:
                try
                {
                    ConfigChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Xử lý báo đổi cấu hình lỗi: {Message}", ex.Message);
                }

                break;
        }
    }

    /// <summary>Chạy lệnh nền để vòng nhận vẫn đọc được khung mới (<c>config-changed</c>) trong lúc lệnh chạy.</summary>
    private async Task RunCommandAsync(ChannelEnvelope command, Func<ChannelEnvelope, Task> send, CancellationToken ct)
    {
        try
        {
            var result = await _dispatcher.RunAsync(command, send, ct);
            try
            {
                await send(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Chưa gửi được kết quả lệnh {Command}: {Message}", command.Name, ex.Message);
            }

            if (result.Ok == true)
            {
                await _dispatcher.AfterResultAsync(command, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lệnh {Command} lỗi ngoài dự kiến: {Message}", command.Name, ex.Message);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Agent đóng kênh", timeout.Token);
            }
        }
        catch (Exception)
        {
            // đã đứt — bỏ qua
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<WebSocket> ConnectRealAsync(Uri url, string agentKey, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader(ServerClient.KeyHeader, agentKey);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(url, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
