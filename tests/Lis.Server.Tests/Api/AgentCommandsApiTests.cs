using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lis.Core.Agents;
using Lis.Core.Instruments;
using Lis.Data.Instruments;
using Lis.Server.AgentApi;
using Lis.Server.Api;
using Lis.Server.Tenancy;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>API ra lệnh agent: kiểm đầu vào, quyền, thu hồi tức thì, lưu cổng COM sau khi quét, báo agent khi sửa máy, tệp cập nhật.</summary>
public sealed class AgentCommandsApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>, IDisposable
{
    private const string Password = "mat-khau-thu-1";
    private const string AdminOnly = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";
    private static readonly Uri ChannelUri = new("ws://localhost/agent/v1/channel");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly List<string> _dirs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed record Setup(ApiHost Host, Clinic Clinic, HttpClient Admin, HttpClient Tech, AgentInfo Agent, string Key) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public string Url => $"/api/agents/{Agent.Id}/commands";
    }

    private async Task<Setup> StartAsync(string code, string? distDir = null)
    {
        var settings = distDir is null ? null : new Dictionary<string, string> { ["Agent:DistDir"] = distDir };
        var host = await ApiHost.StartAsync(fixture, settings: settings);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var (agent, key) = await host.AddAgentWithKeyAsync(clinic, "Agent Thử", "XN-01");
        return new Setup(host, clinic, await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password), agent, key);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string? message = null)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        if (message is not null) body.GetProperty("message").GetString().ShouldBe(message);
    }

    private static async Task<WebSocket> ConnectAsync(ApiHost host, string key)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers[AgentEndpoints.KeyHeader] = key;
        using var cts = new CancellationTokenSource(Wait);
        var socket = await client.ConnectAsync(ChannelUri, cts.Token);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Hello, Version: "1.0.0", MachineName: "MAY-THU"));
        return socket;
    }

    private static async Task SendAsync(WebSocket socket, ChannelEnvelope envelope)
    {
        using var cts = new CancellationTokenSource(Wait);
        await socket.SendAsync(Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(envelope)), WebSocketMessageType.Text, true, cts.Token);
    }

    /// <summary>Nhận một khung; null khi server đóng kênh.</summary>
    private static async Task<ChannelEnvelope?> ReceiveAsync(WebSocket socket, TimeSpan? timeout = null, string what = "một khung từ server")
    {
        using var cts = new CancellationTokenSource(timeout ?? Wait);
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Shouldly.ShouldAssertException($"Hết giờ chờ {what}");
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return ChannelProtocol.Parse(message.ToArray());
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("Hết giờ chờ điều kiện");
            await Task.Delay(150);
        }
    }

    private static Task ConnectedAsync(Setup s) => WaitUntilAsync(() => Task.FromResult(s.Host.Hub.IsConnected(s.Agent.Id)));

    private string NewDistDir()
    {
        var dir = Directory.CreateTempSubdirectory("lis-agent-dist-").FullName;
        _dirs.Add(dir);
        return dir;
    }

    private static object TestPort(Action<Dictionary<string, object>>? change = null)
    {
        var args = new Dictionary<string, object>
        {
            ["portName"] = "COM3", ["baudRate"] = 9600, ["dataBits"] = 8, ["parity"] = "None", ["stopBits"] = 1, ["seconds"] = 10,
        };
        change?.Invoke(args);
        return new { name = "test-port", args };
    }

    [Fact]
    public async Task Ky_thuat_vien_khong_ra_lenh_duoc_nhung_xem_lich_su_duoc()
    {
        await using var s = await StartAsync("lenhktv");

        await ShouldFail(await s.Tech.PostAsJsonAsync(s.Url, new { name = "scan-ports" }, Ct), HttpStatusCode.Forbidden, AdminOnly);
        (await s.Tech.GetAsync(s.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Host.Anonymous.PostAsJsonAsync(s.Url, new { name = "scan-ports" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.GetAsync(s.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ten_lenh_la_va_args_sai_bi_400()
    {
        await using var s = await StartAsync("lenh400");
        var other = Guid.NewGuid();

        async Task Bad(object body) => (await s.Admin.PostAsJsonAsync(s.Url, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await Bad(new { name = "format-c" });
        await Bad(new { });
        await Bad(new { name = "scan-ports", la = 1 });
        await Bad(new { name = "scan-ports", args = new { a = 1 } });
        await Bad(TestPort(a => a["seconds"] = 61));
        await Bad(TestPort(a => a["seconds"] = 0));
        await Bad(TestPort(a => a["portName"] = "/dev/tty"));
        await Bad(TestPort(a => a["baudRate"] = 100));
        await Bad(TestPort(a => a["dataBits"] = 9));
        await Bad(TestPort(a => a["parity"] = "Mark"));
        await Bad(TestPort(a => a["stopBits"] = 3));
        await Bad(TestPort(a => a["la"] = 1));
        await Bad(new { name = "test-port" });
        await Bad(new { name = "reconnect-instrument" });
        await Bad(new { name = "reconnect-instrument", args = new { instrumentId = "xyz" } });
        await Bad(new { name = "reconnect-instrument", args = new { instrumentId = other, la = 1 } });
        await Bad(new { name = "update", args = new { version = "9" } });
        // Máy không gán cho agent này.
        await ShouldFail(
            await s.Admin.PostAsJsonAsync(s.Url, new { name = "reconnect-instrument", args = new { instrumentId = other } }, Ct),
            HttpStatusCode.BadRequest, "Máy này không gán cho agent");
        // Kiểm đầu vào chạy trước kiểm quyền.
        (await s.Tech.PostAsJsonAsync(s.Url, new { name = "format-c" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Admin.PostAsJsonAsync("/api/agents/xyz/commands", new { name = "scan-ports" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Agent_phong_kham_khac_404()
    {
        await using var s = await StartAsync("lenhkhac");
        var clinicB = await s.Host.AddClinicAsync("lenhkhacb", "Phòng khám B Thử");
        var (agentB, _) = await s.Host.AddAgentWithKeyAsync(clinicB, "Agent B Thử", "XN-09");
        var other = await s.Host.TenantOf(clinicB).Instruments.SaveAsync(
            new InstrumentInfo(Guid.Empty, "Máy B Thử", null, null, null, ConnectionSettings.None.ToJson(), "XN-09", true));
        var rowB = await s.Host.AgentCommands.CreateAsync(clinicB.Id, agentB.Id, "scan-ports", null, "queued", null, "Quản trị B", DateTimeOffset.UtcNow);

        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/agents/{agentB.Id}/commands", new { name = "scan-ports" }, Ct), HttpStatusCode.NotFound, "Không tìm thấy agent");
        await ShouldFail(await s.Admin.GetAsync($"/api/agents/{agentB.Id}/commands", Ct), HttpStatusCode.NotFound, "Không tìm thấy agent");
        await ShouldFail(await s.Admin.GetAsync($"/api/agents/commands/{rowB.Id}", Ct), HttpStatusCode.NotFound, "Không tìm thấy lệnh");
        other.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Agent_chua_ghep_noi_409()
    {
        await using var s = await StartAsync("lenhchua");
        var (agent, _) = await s.Host.CreateAgentWithPairingAsync(s.Clinic, "Agent Chờ Thử");

        await ShouldFail(
            await s.Admin.PostAsJsonAsync($"/api/agents/{agent.Id}/commands", new { name = "scan-ports" }, Ct), HttpStatusCode.Conflict, "Agent chưa ghép nối");
    }

    [Fact]
    public async Task Ra_lenh_agent_offline_la_queued_va_xem_lai_duoc()
    {
        await using var s = await StartAsync("lenhoff");

        var response = await s.Admin.PostAsJsonAsync(s.Url, new { name = "scan-ports" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var view = await JsonOf(response);
        view.GetProperty("status").GetString().ShouldBe("queued");
        view.GetProperty("name").GetString().ShouldBe("scan-ports");
        view.GetProperty("requestedBy").GetString().ShouldBe("admin Thử");
        view.GetProperty("args").ValueKind.ShouldBe(JsonValueKind.Null);
        var id = view.GetProperty("id").GetGuid();

        var list = await JsonOf(await s.Tech.GetAsync($"{s.Url}?limit=5", Ct));
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("id").GetGuid().ShouldBe(id);
        (await JsonOf(await s.Tech.GetAsync($"/api/agents/commands/{id}", Ct))).GetProperty("status").GetString().ShouldBe("queued");
    }

    [Fact]
    public async Task Lenh_moi_nhat_truoc_va_args_test_port_dung_lai_tu_gia_tri_da_kiem()
    {
        await using var s = await StartAsync("lenhtest");
        var first = await JsonOf(await s.Admin.PostAsJsonAsync(s.Url, new { name = "restart" }, Ct));
        var second = await JsonOf(await s.Admin.PostAsJsonAsync(s.Url, TestPort(a => a["portName"] = "com7"), Ct));

        second.GetProperty("args").GetProperty("portName").GetString().ShouldBe("COM7");
        second.GetProperty("args").GetProperty("seconds").GetInt32().ShouldBe(10);
        var list = await JsonOf(await s.Admin.GetAsync(s.Url, Ct));
        list[0].GetProperty("id").GetGuid().ShouldBe(second.GetProperty("id").GetGuid());
        list[1].GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Reconnect_instrument_chi_nhan_may_cua_agent_nay()
    {
        await using var s = await StartAsync("lenhrec");
        var mine = await s.Host.TenantOf(s.Clinic).Instruments.SaveAsync(
            new InstrumentInfo(Guid.Empty, "Máy Thử", null, null, null, ConnectionSettings.None.ToJson(), "xn-01", true));

        var response = await s.Admin.PostAsJsonAsync(s.Url, new { name = "reconnect-instrument", args = new { instrumentId = mine } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await JsonOf(response)).GetProperty("args").GetProperty("instrumentId").GetGuid().ShouldBe(mine);
    }

    [Fact]
    public async Task Update_khi_chua_co_tep_409()
    {
        await using var s = await StartAsync("lenhupd0");

        await ShouldFail(await s.Admin.PostAsJsonAsync(s.Url, new { name = "update" }, Ct), HttpStatusCode.Conflict, "Máy chủ chưa có tệp cài agent");
    }

    [Fact]
    public async Task Update_dien_version_sha_tu_server()
    {
        var dir = NewDistDir();
        var content = Encoding.UTF8.GetBytes("MZ-thu-cap-nhat");
        await File.WriteAllBytesAsync(Path.Combine(dir, "LisAgent.exe"), content, Ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "version.txt"), "0.3.0\n", Ct);
        await using var s = await StartAsync("lenhupd", dir);

        var response = await s.Admin.PostAsJsonAsync(s.Url, new { name = "update" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var args = (await JsonOf(response)).GetProperty("args");
        args.GetProperty("version").GetString().ShouldBe("0.3.0");
        args.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    [Fact]
    public async Task Thu_hoi_khi_agent_tat()
    {
        await using var s = await StartAsync("lenhthu0");

        var response = await s.Admin.PostAsJsonAsync(s.Url, new { name = "revoke" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var view = await JsonOf(response);
        view.GetProperty("status").GetString().ShouldBe("done");
        view.GetProperty("result").GetProperty("offline").GetBoolean().ShouldBeTrue();

        (await s.Host.AgentClient(s.Key).GetAsync("/agent/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var ex = await Should.ThrowAsync<Exception>(() => ConnectAsync(s.Host, s.Key));
        ex.ToString().ShouldContain("401");
        var agents = await JsonOf(await s.Admin.GetAsync("/api/agents", Ct));
        agents.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == s.Agent.Id).GetProperty("paired").GetBoolean().ShouldBeFalse();
        // Thu hồi lần nữa: agent đã không còn ghép nối.
        await ShouldFail(await s.Admin.PostAsJsonAsync(s.Url, new { name = "revoke" }, Ct), HttpStatusCode.Conflict, "Agent chưa ghép nối");
    }

    [Fact]
    public async Task Thu_hoi_khi_agent_dang_noi_dong_kenh_sau_ket_qua()
    {
        await using var s = await StartAsync("lenhthu1");
        using var socket = await ConnectAsync(s.Host, s.Key);
        await ConnectedAsync(s);

        var response = await s.Admin.PostAsJsonAsync(s.Url, new { name = "revoke" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var view = await JsonOf(response);
        // Khoá bị từ chối ngay, trước cả khi agent trả lời.
        (await s.Host.AgentClient(s.Key).GetAsync("/agent/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var command = await ReceiveAsync(socket);
        command.ShouldNotBeNull();
        command.Name.ShouldBe("revoke");
        command.Id.ShouldBe(view.GetProperty("id").GetGuid());

        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Result, command.Id, Ok: true));

        (await ReceiveAsync(socket, TimeSpan.FromSeconds(30), "khung đóng kênh sau thu hồi")).ShouldBeNull();
        await WaitUntilAsync(() => Task.FromResult(!s.Host.Hub.IsConnected(s.Agent.Id)));
        (await s.Host.AgentCommands.FindAsync(s.Clinic.Id, command.Id!.Value))!.Status.ShouldBe("done");
    }

    [Fact]
    public async Task Thu_hoi_khi_agent_khong_tra_loi_van_dong_kenh_sau_5_giay()
    {
        await using var s = await StartAsync("lenhthu2");
        using var socket = await ConnectAsync(s.Host, s.Key);
        await ConnectedAsync(s);

        var before = AgentCommandsEndpoints.RevokeCloseGrace;
        AgentCommandsEndpoints.RevokeCloseGrace = TimeSpan.FromMilliseconds(300);
        try
        {
            (await s.Admin.PostAsJsonAsync(s.Url, new { name = "revoke" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

            (await ReceiveAsync(socket))!.Name.ShouldBe("revoke");
        // Agent im lặng: server vẫn đóng kênh, không giữ mãi.
            (await ReceiveAsync(socket, TimeSpan.FromSeconds(30), "khung đóng kênh (agent im lặng)")).ShouldBeNull();
        }
        finally
        {
            AgentCommandsEndpoints.RevokeCloseGrace = before;
        }
    }

    [Fact]
    public async Task Quet_cong_xong_luu_ports()
    {
        await using var s = await StartAsync("lenhcom");
        using var socket = await ConnectAsync(s.Host, s.Key);
        await ConnectedAsync(s);
        var view = await JsonOf(await s.Admin.PostAsJsonAsync(s.Url, new { name = "scan-ports" }, Ct));
        var command = (await ReceiveAsync(socket))!;
        command.Id.ShouldBe(view.GetProperty("id").GetGuid());

        var data = JsonDocument.Parse("""{"ports":[{"name":"COM3","description":"USB-SERIAL CH340","usedBy":null,"busy":false}]}""").RootElement;
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Result, command.Id, Ok: true, Data: data));

        JsonElement agent = default;
        await WaitUntilAsync(async () =>
        {
            var agents = await JsonOf(await s.Admin.GetAsync("/api/agents", Ct));
            agent = agents.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == s.Agent.Id).Clone();
            return agent.GetProperty("ports").ValueKind == JsonValueKind.Array;
        });
        agent.GetProperty("ports")[0].GetProperty("name").GetString().ShouldBe("COM3");
        agent.GetProperty("ports")[0].GetProperty("description").GetString().ShouldBe("USB-SERIAL CH340");
        agent.GetProperty("portsScannedAt").GetString().ShouldNotBeNullOrEmpty();
        agent.GetProperty("channel").GetBoolean().ShouldBeTrue();
        agent.GetProperty("online").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Agent_chua_quet_thi_ports_null_va_khong_co_kenh()
    {
        await using var s = await StartAsync("lenhnull");

        var agent = (await JsonOf(await s.Admin.GetAsync("/api/agents", Ct))).EnumerateArray().Single();

        agent.GetProperty("ports").ValueKind.ShouldBe(JsonValueKind.Null);
        agent.GetProperty("portsScannedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        agent.GetProperty("channel").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Sua_may_bao_agent_doi_cau_hinh()
    {
        await using var s = await StartAsync("lenhcfg");
        var (other, otherKey) = await s.Host.AddAgentWithKeyAsync(s.Clinic, "Agent Khác Thử", "XN-02");
        using var socket = await ConnectAsync(s.Host, s.Key);
        using var otherSocket = await ConnectAsync(s.Host, otherKey);
        await ConnectedAsync(s);
        await WaitUntilAsync(() => Task.FromResult(s.Host.Hub.IsConnected(other.Id)));

        object Body(string workstation) => new
        {
            name = "Máy Thử", active = true, workstationCode = workstation, connection = new { kind = "none" },
        };

        var created = await s.Admin.PostAsJsonAsync("/api/instruments", Body("XN-01"), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReceiveAsync(socket))!.Type.ShouldBe("config-changed");
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();

        // Đổi sang máy trạm khác: cả agent cũ lẫn agent mới đều được báo.
        (await s.Admin.PutAsJsonAsync($"/api/instruments/{id}", Body("XN-02"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReceiveAsync(socket))!.Type.ShouldBe("config-changed");
        (await ReceiveAsync(otherSocket))!.Type.ShouldBe("config-changed");

        (await s.Admin.DeleteAsync($"/api/instruments/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReceiveAsync(otherSocket))!.Type.ShouldBe("config-changed");
    }

    [Fact]
    public async Task Agent_tai_tep_cap_nhat()
    {
        var dir = NewDistDir();
        var content = Encoding.UTF8.GetBytes("MZ-thu-tai");
        await File.WriteAllBytesAsync(Path.Combine(dir, "LisAgent.exe"), content, Ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "version.txt"), "0.4.0", Ct);
        await using var s = await StartAsync("lenhtai", dir);

        var response = await s.Host.AgentClient(s.Key).GetAsync("/agent/v1/installer", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Agent-Sha256").Single().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
        response.Headers.GetValues("X-Agent-Version").Single().ShouldBe("0.4.0");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(content);
        (await s.Host.AgentClient().GetAsync("/agent/v1/installer", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agent_tai_tep_khi_chua_co_tep_404()
    {
        await using var s = await StartAsync("lenhtai0");

        (await s.Host.AgentClient(s.Key).GetAsync("/agent/v1/installer", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
