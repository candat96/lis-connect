using System.Net.WebSockets;
using System.Text.Json;
using Lis.Core.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class ChannelClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-agent-chan-" + Guid.NewGuid().ToString("N"));

    public ChannelClientTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Backoff()
    {
        double[] expected = [1, 2, 4, 8, 16, 30, 60, 60];
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            ChannelClient.BackoffFor(attempt, 0.5).TotalSeconds.ShouldBe(expected[attempt], 1e-9);
            ChannelClient.BackoffFor(attempt, 0).TotalSeconds.ShouldBe(expected[attempt] * 0.8, 1e-9);
            ChannelClient.BackoffFor(attempt, 1).TotalSeconds.ShouldBe(expected[attempt] * 1.2, 1e-9);
        }

        ChannelClient.BackoffFor(6, 1).TotalSeconds.ShouldBe(72, 1e-9);
        ChannelClient.BackoffFor(50, 1).TotalSeconds.ShouldBe(72, 1e-9);
    }

    [Theory]
    [InlineData("https://lis.vn/", "wss://lis.vn/agent/v1/channel")]
    [InlineData("https://lis.vn", "wss://lis.vn/agent/v1/channel")]
    [InlineData("http://10.0.0.5:5080", "ws://10.0.0.5:5080/agent/v1/channel")]
    [InlineData("http://may-chu.thu", "ws://may-chu.thu/agent/v1/channel")]
    [InlineData("https://lis.vn/pk/", "wss://lis.vn/pk/agent/v1/channel")]
    public void Url(string server, string expected) =>
        ChannelClient.ChannelUrl(new Uri(server)).ToString().ShouldBe(expected);

    private ChannelClient NewClient(
        CommandDispatcher dispatcher, FakeTimeProvider clock, Func<Uri, string, CancellationToken, Task<WebSocket>> connect, Action<string>? note = null) =>
        new(new Uri("http://may-chu.thu:5080"), "KHOA-BI-MAT", dispatcher, new AgentPaths(_root), clock, NullLogger.Instance, connect, note);

    [Fact]
    public async Task Hello_ghi_dau_noi_va_gui_ket_qua_cap_nhat_roi_xoa_tep()
    {
        var paths = new AgentPaths(_root);
        var update = new UpdateReport(Guid.NewGuid(), false, "0.1.0", "Bản mới không chạy");
        File.WriteAllText(paths.UpdateReport, JsonSerializer.Serialize(update, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var socket = new FakeWebSocket();
        Uri? seenUrl = null;
        string? seenKey = null;
        var client = NewClient(new CommandDispatcher([]), new FakeTimeProvider(), (url, key, _) =>
        {
            seenUrl = url;
            seenKey = key;
            return Task.FromResult<WebSocket>(socket);
        });

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await Polling.UntilAsync(() => socket.SentSnapshot().Length == 1 && File.Exists(paths.ConnectedMarker), "hello");

        var hello = socket.SentSnapshot()[0];
        hello.Type.ShouldBe(ChannelProtocol.Hello);
        hello.Version.ShouldBe(AgentVersion.Current);
        hello.MachineName.ShouldBe(Environment.MachineName);
        hello.Update.ShouldBe(update);
        File.Exists(paths.UpdateReport).ShouldBeFalse();
        using var marker = JsonDocument.Parse(File.ReadAllText(paths.ConnectedMarker));
        marker.RootElement.GetProperty("version").GetString().ShouldBe(AgentVersion.Current);
        marker.RootElement.TryGetProperty("at", out _).ShouldBeTrue();

        // Khoá không nằm trong URL.
        seenKey.ShouldBe("KHOA-BI-MAT");
        seenUrl!.ToString().ShouldBe("ws://may-chu.thu:5080/agent/v1/channel");
        seenUrl.ToString().ShouldNotContain("KHOA");

        await cts.CancelAsync();
        await run.WaitAsync(Polling.Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Lenh_chay_ra_started_roi_result_va_config_changed_goi_su_kien()
    {
        var socket = new FakeWebSocket();
        var log = new List<string>();
        var client = NewClient(new CommandDispatcher([new RecordingHandler("reload-config", log)]), new FakeTimeProvider(),
            (_, _, _) => Task.FromResult<WebSocket>(socket));
        var changed = 0;
        client.ConfigChanged += () => Interlocked.Increment(ref changed);

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await Polling.UntilAsync(() => socket.SentSnapshot().Length == 1, "hello");

        var id = Guid.NewGuid();
        socket.Push(new ChannelEnvelope(ChannelProtocol.Command, id, "reload-config"));
        socket.Push(new ChannelEnvelope(ChannelProtocol.ConfigChanged));
        await Polling.UntilAsync(() => socket.SentSnapshot().Length == 3 && Volatile.Read(ref changed) == 1, "started + result + config-changed");

        var sent = socket.SentSnapshot();
        sent[1].Type.ShouldBe(ChannelProtocol.Started);
        sent[1].Id.ShouldBe(id);
        sent[2].Type.ShouldBe(ChannelProtocol.Result);
        sent[2].Ok.ShouldBe(true);

        await cts.CancelAsync();
        await run.WaitAsync(Polling.Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dut_kenh_thi_noi_lai_va_hello_lai()
    {
        var sockets = new List<FakeWebSocket>();
        var clock = new FakeTimeProvider();
        var client = NewClient(new CommandDispatcher([]), clock, (_, _, _) =>
        {
            var socket = new FakeWebSocket();
            lock (sockets)
            {
                sockets.Add(socket);
            }

            return Task.FromResult<WebSocket>(socket);
        });

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await Polling.UntilAsync(() => { lock (sockets) { return sockets.Count == 1 && sockets[0].SentSnapshot().Length == 1; } }, "hello lần 1");
        sockets[0].ServerClose();
        await Polling.UntilAsync(() => { lock (sockets) { return sockets.Count == 2 && sockets[1].SentSnapshot().Length == 1; } }, "hello lần 2", () => clock.Advance(TimeSpan.FromSeconds(2)));

        await cts.CancelAsync();
        await run.WaitAsync(Polling.Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Mat_kenh_khong_dung_may()
    {
        var manager = new FakeConnections();
        var clock = new FakeTimeProvider();
        var attempts = 0;
        var notes = new List<string>();
        var client = NewClient(new CommandDispatcher([]), clock, (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("Không nối được máy chủ thử");
        }, notes.Add);

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        await Polling.UntilAsync(() => Volatile.Read(ref attempts) >= 3, "3 lần thử nối", () => clock.Advance(TimeSpan.FromSeconds(80)));

        run.IsCompleted.ShouldBeFalse(); // vẫn đang thử lại, không văng ra ngoài
        manager.StopAlls.ShouldBe(0);
        manager.Reloads.ShouldBe(0);
        notes.Count.ShouldBe(1); // chỉ báo lần đầu mất kênh
        notes[0].ShouldNotContain("KHOA-BI-MAT");

        await cts.CancelAsync();
        await run.WaitAsync(Polling.Wait, TestContext.Current.CancellationToken);
        manager.StopAlls.ShouldBe(0);
    }
}
