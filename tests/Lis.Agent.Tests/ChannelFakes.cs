using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Lis.Connections;
using Lis.Core.Agents;
using Xunit;

namespace Lis.Agent.Tests;

/// <summary>Bộ quản lý kết nối máy giả: đếm số lần được gọi.</summary>
internal sealed class FakeConnections : IInstrumentConnections
{
    public int Reloads { get; private set; }

    public int StopAlls { get; private set; }

    public List<Guid> Restarts { get; } = [];

    public int Remaining { get; set; }

    public Exception? ReloadFails { get; set; }

    public event Action<InstrumentConnectionStatus>? StatusChanged;

    public InstrumentConnectionStatus StatusOf(Guid instrumentId) => new(instrumentId, LinkState.NotRunning, "giả");

    public Task ReloadAsync()
    {
        Reloads++;
        return ReloadFails is null ? Task.CompletedTask : Task.FromException(ReloadFails);
    }

    public Task RestartAsync(Guid instrumentId)
    {
        Restarts.Add(instrumentId);
        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        StopAlls++;
        return Task.CompletedTask;
    }

    public Task<int> RetryPendingNowAsync() => Task.FromResult(Remaining);

    public void Raise(InstrumentConnectionStatus status) => StatusChanged?.Invoke(status);
}

internal sealed class FakeLifetime : IAgentLifetime
{
    public int Restarts { get; private set; }

    public int Revokes { get; private set; }

    public void RestartSoon() => Restarts++;

    public void StopForRevoke() => Revokes++;
}

/// <summary>WebSocket giả trong bộ nhớ: server "đẩy" khung vào <see cref="Incoming"/>, khung agent gửi nằm ở <see cref="Sent"/>.</summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
    private WebSocketState _state = WebSocketState.Open;

    public List<ChannelEnvelope> Sent { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    /// <summary>Server gửi một khung.</summary>
    public void Push(ChannelEnvelope envelope) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(envelope)));

    /// <summary>Server đóng kênh.</summary>
    public void ServerClose() => _incoming.Writer.TryWrite(null);

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var frame = await _incoming.Reader.ReadAsync(cancellationToken);
        if (frame is null)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        frame.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(ChannelProtocol.Parse(buffer.AsSpan())!);
        }

        return Task.CompletedTask;
    }

    public ChannelEnvelope[] SentSnapshot()
    {
        lock (Sent)
        {
            return [.. Sent];
        }
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override void Abort() => _state = WebSocketState.Aborted;

    public override void Dispose() => _state = WebSocketState.Closed;
}

/// <summary>Xử lý lệnh giả: ghi thứ tự bắt đầu / kết thúc để kiểm chạy tuần tự.</summary>
internal sealed class RecordingHandler(string name, List<string> log, Func<Task>? body = null) : IAgentCommandHandler
{
    public string Name => name;

    public async Task<System.Text.Json.JsonElement?> RunAsync(System.Text.Json.JsonElement? args, CancellationToken ct)
    {
        log.Add("bắt đầu " + args?.GetRawText());
        if (body is not null)
        {
            await body();
        }

        log.Add("xong " + args?.GetRawText());
        return null;
    }
}

internal static class Polling
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<bool> condition, string what, Action? tick = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Shouldly.ShouldAssertException($"Hết giờ chờ {what}");
            }

            tick?.Invoke();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
