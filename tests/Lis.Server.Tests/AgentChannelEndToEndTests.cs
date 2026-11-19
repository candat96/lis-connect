using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Lis.Agent;
using Lis.Connections;
using Lis.Core.Agents;
using Lis.Server.AgentApi;
using Lis.Server.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Đường đi thật của kênh lệnh: server thật + <see cref="ChannelClient"/> thật + lệnh thật của agent (bộ quản lý máy là bản giả).</summary>
public sealed class AgentChannelEndToEndTests(ServerFixture fixture) : IClassFixture<ServerFixture>, IDisposable
{
    private const string Password = "mat-khau-thu-1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _root = Directory.CreateTempSubdirectory("lis-agent-e2e-chan-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CountingConnections : IInstrumentConnections
    {
        public int Reloads;

        public event Action<InstrumentConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public InstrumentConnectionStatus StatusOf(Guid instrumentId) => new(instrumentId, LinkState.NotRunning, "giả");

        public Task ReloadAsync()
        {
            Interlocked.Increment(ref Reloads);
            return Task.CompletedTask;
        }

        public Task RestartAsync(Guid instrumentId) => Task.CompletedTask;

        public Task StopAllAsync() => Task.CompletedTask;

        public Task<int> RetryPendingNowAsync() => Task.FromResult(0);

        public Task<PortLease> BorrowPortAsync(string portName, CancellationToken ct = default) => Task.FromResult(new PortLease([]));
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required ApiHost Host { get; init; }

        public required Clinic Clinic { get; init; }

        public required AgentInfo Agent { get; init; }

        public required HttpClient Admin { get; init; }

        public required CountingConnections Manager { get; init; }

        public required ChannelClient Client { get; init; }

        public required CancellationTokenSource Stop { get; init; }

        public required Task Run { get; init; }

        public required AgentPaths Paths { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            await Run.WaitAsync(Wait);
            Stop.Dispose();
            await Host.DisposeAsync();
        }
    }

    private async Task<Rig> StartAsync(string code)
    {
        var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        var (agent, key) = await host.AddAgentWithKeyAsync(clinic, "Agent Thử", "XN-01");
        var admin = await host.LoginAsync(code, "admin", Password);

        var manager = new CountingConnections();
        var paths = new AgentPaths(_root);
        var runtime = new AgentRuntime { Manager = manager, WorkstationCode = "XN-01" };
        var dispatcher = new CommandDispatcher(
        [
            new ReloadConfigHandler(runtime), new FlushPendingHandler(runtime), new ReconnectInstrumentHandler(runtime),
        ]);

        var sockets = new List<WebSocket>();
        var client = new ChannelClient(
            new Uri("http://localhost/"), key, dispatcher, paths, TimeProvider.System, NullLogger.Instance,
            async (url, agentKey, ct) =>
            {
                var web = host.Server.CreateWebSocketClient();
                web.ConfigureRequest = r => r.Headers[ServerClient.KeyHeader] = agentKey;
                var socket = await web.ConnectAsync(url, ct);
                lock (sockets)
                {
                    sockets.Add(socket);
                }

                return socket;
            });
        client.ConfigChanged += () => _ = runtime.Manager!.ReloadAsync();

        var stop = new CancellationTokenSource();
        var rig = new Rig
        {
            Host = host, Clinic = clinic, Agent = agent, Admin = admin, Manager = manager, Client = client, Stop = stop,
            Run = client.RunAsync(stop.Token), Paths = paths,
        };
        _sockets = sockets;
        return rig;
    }

    private List<WebSocket> _sockets = [];

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue($"Hết giờ chờ {what}");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<JsonElement> AgentViewAsync(Rig rig)
    {
        var agents = await rig.Admin.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        return agents.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == rig.Agent.Id).Clone();
    }

    private static async Task<string> CommandStatusAsync(Rig rig, Guid id)
    {
        var list = await rig.Admin.GetFromJsonAsync<JsonElement>($"/api/agents/{rig.Agent.Id}/commands", Ct);
        return list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task Hello_web_thay_kenh_va_phien_ban()
    {
        await using var rig = await StartAsync("e2ehello");

        await WaitUntilAsync(async () => (await AgentViewAsync(rig)).GetProperty("channel").GetBoolean(), "kênh nối");
        await WaitUntilAsync(async () => (await AgentViewAsync(rig)).GetProperty("version").GetString() == AgentVersion.Current, "phiên bản từ hello");
    }

    [Fact]
    public async Task Lenh_reload_config_tu_web_chay_xong_va_ghi_ket_qua()
    {
        await using var rig = await StartAsync("e2ereload");
        await WaitUntilAsync(() => Task.FromResult(rig.Host.Hub.IsConnected(rig.Agent.Id)), "kênh nối");

        var response = await rig.Admin.PostAsJsonAsync($"/api/agents/{rig.Agent.Id}/commands", new { name = "reload-config" }, Ct);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        await WaitUntilAsync(async () => await CommandStatusAsync(rig, id) == "done", "lệnh xong");
        rig.Manager.Reloads.ShouldBe(1);
    }

    [Fact]
    public async Task Lenh_la_chua_ho_tro_bao_failed()
    {
        await using var rig = await StartAsync("e2eunsupported");
        await WaitUntilAsync(() => Task.FromResult(rig.Host.Hub.IsConnected(rig.Agent.Id)), "kênh nối");

        var response = await rig.Admin.PostAsJsonAsync($"/api/agents/{rig.Agent.Id}/commands", new { name = "scan-ports" }, Ct);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        await WaitUntilAsync(async () => await CommandStatusAsync(rig, id) == "failed", "lệnh báo lỗi");
        var list = await rig.Admin.GetFromJsonAsync<JsonElement>($"/api/agents/{rig.Agent.Id}/commands", Ct);
        list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("error").GetString()
            .ShouldBe("Agent chưa hỗ trợ lệnh scan-ports");
    }

    [Fact]
    public async Task Sua_may_tren_web_agent_nap_lai_cau_hinh()
    {
        await using var rig = await StartAsync("e2econfig");
        await WaitUntilAsync(() => Task.FromResult(rig.Host.Hub.IsConnected(rig.Agent.Id)), "kênh nối");

        var created = await rig.Admin.PostAsJsonAsync(
            "/api/instruments",
            new { name = "Máy Thử", active = true, workstationCode = "XN-01", connection = new { kind = "none" } }, Ct);
        created.EnsureSuccessStatusCode();

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref rig.Manager.Reloads) >= 1), "agent nạp lại sau config-changed");
    }

    [Fact]
    public async Task Dut_kenh_agent_noi_lai_va_ghi_dau_noi()
    {
        await using var rig = await StartAsync("e2ereconnect");
        await WaitUntilAsync(() => Task.FromResult(rig.Host.Hub.IsConnected(rig.Agent.Id) && File.Exists(rig.Paths.ConnectedMarker)), "kênh nối lần 1");
        File.Delete(rig.Paths.ConnectedMarker);

        WebSocket first;
        lock (_sockets)
        {
            first = _sockets.Single();
        }

        first.Abort(); // kênh đứt đột ngột (mạng rớt)

        await WaitUntilAsync(() => Task.FromResult(File.Exists(rig.Paths.ConnectedMarker)), "nối lại và ghi ket-noi.json");
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(rig.Paths.ConnectedMarker, Ct));
        marker.RootElement.GetProperty("version").GetString().ShouldBe(AgentVersion.Current);
        await WaitUntilAsync(() => Task.FromResult(rig.Host.Hub.IsConnected(rig.Agent.Id)), "server thấy kênh mới");
        lock (_sockets)
        {
            _sockets.Count.ShouldBe(2);
        }
    }
}
