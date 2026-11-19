using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lis.Core.Agents;
using Lis.Server.AgentApi;
using Lis.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Kênh lệnh WebSocket <c>/agent/v1/channel</c>: bắt tay, giao lệnh, hết hạn 60 giây, đứt kênh, kết nối mới thay cũ, khởi động lại.</summary>
public sealed class AgentChannelTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static readonly Uri ChannelUri = new("ws://localhost/agent/v1/channel");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.FromHours(7)));

    private static async Task<WebSocket> ConnectAsync(ApiHost host, string key)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers[AgentEndpoints.KeyHeader] = key;
        using var cts = new CancellationTokenSource(Wait);
        return await client.ConnectAsync(ChannelUri, cts.Token);
    }

    private static async Task SendAsync(WebSocket socket, ChannelEnvelope envelope)
    {
        using var cts = new CancellationTokenSource(Wait);
        await socket.SendAsync(Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(envelope)), WebSocketMessageType.Text, true, cts.Token);
    }

    /// <summary>Nhận một khung; null khi server đóng kênh.</summary>
    private static async Task<ChannelEnvelope?> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
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
            await Task.Delay(20);
        }
    }

    private static async Task<(ApiHost Host, Clinic Clinic, AgentInfo Agent, string Key, FakeTimeProvider Clock)> StartAsync(ServerFixture fixture, string code)
    {
        var clock = NewClock();
        var host = await ApiHost.StartAsync(fixture, clock: clock);
        var clinic = await host.AddClinicAsync(code, $"Phòng khám {code} Thử");
        var (agent, key) = await host.AddAgentWithKeyAsync(clinic, "Agent Thử", "XN-01");
        return (host, clinic, agent, key, clock);
    }

    private static Task<AgentCommandRow?> RowAsync(ApiHost host, Clinic clinic, Guid id) => host.AgentCommands.FindAsync(clinic.Id, id);

    [Fact]
    public async Task Khoa_sai_bi_tu_choi()
    {
        var (host, _, agent, _, _) = await StartAsync(fixture, "kenhsai");
        await using var _h = host;

        var wrong = $"lisa_{agent.Id:N}_{new string('0', 64)}";
        var ex = await Should.ThrowAsync<Exception>(() => ConnectAsync(host, wrong));
        ex.ToString().ShouldContain("401");
        host.Hub.IsConnected(agent.Id).ShouldBeFalse();

        await Should.ThrowAsync<Exception>(() => ConnectAsync(host, "khong-phai-khoa"));
    }

    [Fact]
    public async Task Khong_phai_websocket_tra_400()
    {
        var (host, _, _, key, _) = await StartAsync(fixture, "kenh400");
        await using var _h = host;

        var response = await host.AgentClient(key).GetAsync("/agent/v1/channel", TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBe(400);
    }

    [Fact]
    public async Task Lenh_den_agent_dang_noi_va_ket_qua_ve_lich_su()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhnoi");
        await using var _h = host;
        var observed = new System.Collections.Concurrent.ConcurrentQueue<(Guid AgentId, AgentCommandRow Row, ChannelEnvelope Result)>();
        host.Hub.ResultObserver = (agentId, row, result) =>
        {
            observed.Enqueue((agentId, row, result));
            return Task.CompletedTask;
        };

        using var socket = await ConnectAsync(host, key);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Hello, Version: "1.0.0", MachineName: "MAY-THU"));
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));

        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "scan-ports", null, null, "Quản trị Thử");
        row.Status.ShouldBe("sent");

        var command = await ReceiveAsync(socket);
        command.ShouldNotBeNull();
        command.Type.ShouldBe("command");
        command.Id.ShouldBe(row.Id);
        command.Name.ShouldBe("scan-ports");

        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Started, row.Id));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "running");

        var data = JsonDocument.Parse("""{"ports":[]}""").RootElement;
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Result, row.Id, Ok: true, Data: data));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "done");

        var done = (await RowAsync(host, clinic, row.Id))!;
        done.Result!.Value.GetProperty("ports").GetArrayLength().ShouldBe(0);
        done.Error.ShouldBeNull();
        host.Hub.IsConnected(agent.Id).ShouldBeTrue();
        await WaitUntilAsync(() => Task.FromResult(observed.Count == 1));
        var seen = observed.Single();
        seen.AgentId.ShouldBe(agent.Id);
        seen.Row.Name.ShouldBe("scan-ports");
        seen.Row.Status.ShouldBe("done");
        seen.Result.Id.ShouldBe(row.Id);

        // hello cập nhật phiên bản / tên máy
        var info = (await host.Master.AgentsOfAsync(clinic.Id, host.Clock.GetUtcNow())).Single();
        info.AgentVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Ket_qua_that_bai_ghi_failed_kem_loi()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhloi");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "restart", null, null, null);
        (await ReceiveAsync(socket))!.Id.ShouldBe(row.Id);

        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Result, row.Id, Ok: false, Error: "Không mở được cổng"));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "failed");
        (await RowAsync(host, clinic, row.Id))!.Error.ShouldBe("Không mở được cổng");
    }

    [Fact]
    public async Task Agent_khong_dong_vao_lenh_cua_agent_khac()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhcheo");
        await using var _h = host;
        var (other, _) = await host.AddAgentWithKeyAsync(clinic, "Agent khác Thử", "XN-02");
        var foreign = await host.Hub.SubmitAsync(clinic.Id, other.Id, "restart", null, null, null);
        foreign.Status.ShouldBe("queued");

        using var socket = await ConnectAsync(host, key);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Result, foreign.Id, Ok: true));
        // khung kế tiếp hợp lệ để chắc server đã xử lý khung trước
        var own = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "restart", null, null, null);
        (await ReceiveAsync(socket))!.Id.ShouldBe(own.Id);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Started, own.Id));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, own.Id))!.Status == "running");

        (await RowAsync(host, clinic, foreign.Id))!.Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Lenh_cho_agent_offline_giao_khi_noi_trong_60_giay()
    {
        var (host, clinic, agent, key, clock) = await StartAsync(fixture, "kenhcho");
        await using var _h = host;

        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "reload-config", null, null, "Quản trị Thử");
        row.Status.ShouldBe("queued");

        clock.Advance(TimeSpan.FromSeconds(30));
        using var socket = await ConnectAsync(host, key);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Hello, Version: "1.0.0"));

        var command = await ReceiveAsync(socket);
        command.ShouldNotBeNull();
        command.Id.ShouldBe(row.Id);
        command.Name.ShouldBe("reload-config");
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "sent");
    }

    [Fact]
    public async Task Lenh_cho_qua_60_giay_het_han()
    {
        var (host, clinic, agent, key, clock) = await StartAsync(fixture, "kenhhet");
        await using var _h = host;

        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "reload-config", null, null, null);
        clock.Advance(TimeSpan.FromSeconds(61));
        await host.Sweeper.SweepOnceAsync();

        var expired = (await RowAsync(host, clinic, row.Id))!;
        expired.Status.ShouldBe("expired");
        expired.Error.ShouldBe("Agent không kết nối trong 60 giây");

        using var socket = await ConnectAsync(host, key);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Hello));
        // lệnh mới sau hello đến bình thường; lệnh hết hạn thì không
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));
        var next = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "restart", null, null, null);
        (await ReceiveAsync(socket))!.Id.ShouldBe(next.Id);
        (await RowAsync(host, clinic, row.Id))!.Status.ShouldBe("expired");
    }

    [Fact]
    public async Task Dut_giua_chung_thanh_unknown()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhdut");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "scan-ports", null, null, null);
        (await ReceiveAsync(socket))!.Id.ShouldBe(row.Id);
        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Started, row.Id));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "running");

        socket.Abort();

        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "unknown");
        (await RowAsync(host, clinic, row.Id))!.Error.ShouldBe("Agent mất kết nối giữa chừng");
        await WaitUntilAsync(() => Task.FromResult(!host.Hub.IsConnected(agent.Id)));
    }

    [Fact]
    public async Task Ket_noi_moi_thang_ket_noi_cu()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhthay");
        await using var _h = host;
        using var a = await ConnectAsync(host, key);
        await SendAsync(a, new ChannelEnvelope(ChannelProtocol.Hello));
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));

        using var b = await ConnectAsync(host, key);
        await SendAsync(b, new ChannelEnvelope(ChannelProtocol.Hello));

        // A bị đóng
        (await ReceiveAsync(a)).ShouldBeNull();
        a.CloseStatusDescription.ShouldBe("Có kết nối mới hơn");

        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "scan-ports", null, null, null);
        row.Status.ShouldBe("sent");
        (await ReceiveAsync(b))!.Id.ShouldBe(row.Id);

        // Kết quả từ B được áp dụng; A đóng nốt không làm lệnh của B thành unknown.
        await SendAsync(b, new ChannelEnvelope(ChannelProtocol.Started, row.Id));
        await a.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "đóng", TestContext.Current.CancellationToken);
        await SendAsync(b, new ChannelEnvelope(ChannelProtocol.Result, row.Id, Ok: true));
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "done");
        host.Hub.IsConnected(agent.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Lenh_gui_vao_ket_noi_cu_truoc_khi_thay_thanh_unknown()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhcu");
        await using var _h = host;
        using var a = await ConnectAsync(host, key);
        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "scan-ports", null, null, null);
        (await ReceiveAsync(a))!.Id.ShouldBe(row.Id);

        using var b = await ConnectAsync(host, key);
        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "unknown");
        (await RowAsync(host, clinic, row.Id))!.Error.ShouldBe("Agent mất kết nối giữa chừng");
        host.Hub.IsConnected(agent.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Tin_nhan_chia_nhieu_khung_duoc_ghep_lai()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhghep");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "scan-ports", null, null, null);
        (await ReceiveAsync(socket))!.Id.ShouldBe(row.Id);

        var bytes = Encoding.UTF8.GetBytes(ChannelProtocol.Serialize(new ChannelEnvelope(
            ChannelProtocol.Result, row.Id, Ok: true, Data: JsonDocument.Parse("""{"ports":["COM3"]}""").RootElement)));
        var cut1 = bytes.Length / 3;
        var cut2 = 2 * bytes.Length / 3;
        var ct = TestContext.Current.CancellationToken;
        await socket.SendAsync(bytes.AsMemory(0, cut1), WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(bytes.AsMemory(cut1, cut2 - cut1), WebSocketMessageType.Text, false, ct);
        await socket.SendAsync(bytes.AsMemory(cut2), WebSocketMessageType.Text, true, ct);

        await WaitUntilAsync(async () => (await RowAsync(host, clinic, row.Id))!.Status == "done");
        (await RowAsync(host, clinic, row.Id))!.Result!.Value.GetProperty("ports")[0].GetString().ShouldBe("COM3");
    }

    [Fact]
    public async Task Khung_nhi_phan_bi_dong()
    {
        var (host, _, agent, key, _) = await StartAsync(fixture, "kenhnhiphan");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);

        (await ReceiveAsync(socket)).ShouldBeNull();
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        await WaitUntilAsync(() => Task.FromResult(!host.Hub.IsConnected(agent.Id)));
    }

    [Fact]
    public async Task Lenh_gui_sau_khi_noi_truoc_hello_chi_giao_mot_lan()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhtruochello");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        var row = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "reload-config", null, null, null);
        row.Status.ShouldBe("sent");

        await SendAsync(socket, new ChannelEnvelope(ChannelProtocol.Hello));
        var after = await host.Hub.SubmitAsync(clinic.Id, agent.Id, "restart", null, null, null);

        // Khung kế tiếp phải là lệnh sau, không có bản giao lần hai của lệnh đầu.
        (await ReceiveAsync(socket))!.Id.ShouldBe(row.Id);
        (await ReceiveAsync(socket))!.Id.ShouldBe(after.Id);
        (await host.AgentCommands.ListAsync(clinic.Id, agent.Id, 50)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Khung_rac_dong_kenh_khong_lam_hong_server()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhrac");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        using (var cts = new CancellationTokenSource(Wait))
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes("không phải json"), WebSocketMessageType.Text, true, cts.Token);
        }

        (await ReceiveAsync(socket)).ShouldBeNull();
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        await WaitUntilAsync(() => Task.FromResult(!host.Hub.IsConnected(agent.Id)));

        // server vẫn nhận kết nối mới
        using var again = await ConnectAsync(host, key);
        await SendAsync(again, new ChannelEnvelope(ChannelProtocol.Hello));
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));
    }

    [Fact]
    public async Task Khung_qua_lon_bi_dong()
    {
        var (host, _, agent, key, _) = await StartAsync(fixture, "kenhlon");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);

        using (var cts = new CancellationTokenSource(Wait))
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var sent = 0; sent <= ChannelProtocol.MaxMessageBytes; sent += chunk.Length)
            {
                try
                {
                    await socket.SendAsync(chunk, WebSocketMessageType.Text, false, cts.Token);
                }
                catch (WebSocketException)
                {
                    break;
                }
            }
        }

        await WaitUntilAsync(() => Task.FromResult(!host.Hub.IsConnected(agent.Id)));
    }

    [Fact]
    public async Task Thong_bao_doi_cau_hinh_chi_gui_cho_agent_dang_noi()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhcfg");
        await using var _h = host;
        var (other, _) = await host.AddAgentWithKeyAsync(clinic, "Agent khác Thử", "XN-02");
        using var socket = await ConnectAsync(host, key);
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));

        await host.Hub.NotifyConfigChangedAsync([agent.Id, other.Id]);

        (await ReceiveAsync(socket))!.Type.ShouldBe("config-changed");
        (await host.AgentCommands.ListAsync(clinic.Id, agent.Id, 50)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Ngat_ket_noi_khi_thu_hoi_dong_kenh()
    {
        var (host, clinic, agent, key, _) = await StartAsync(fixture, "kenhngat");
        await using var _h = host;
        using var socket = await ConnectAsync(host, key);
        await WaitUntilAsync(() => Task.FromResult(host.Hub.IsConnected(agent.Id)));

        await host.Hub.DisconnectAsync(agent.Id);

        (await ReceiveAsync(socket)).ShouldBeNull();
        await WaitUntilAsync(() => Task.FromResult(!host.Hub.IsConnected(agent.Id)));
    }

    [Fact]
    public async Task Khoi_dong_lai_server_danh_dau_unknown()
    {
        var (first, clinic, agent, _, _) = await StartAsync(fixture, "kenhkhoidong");
        await using var _h = first;
        var store = first.AgentCommands;
        var now = DateTimeOffset.UtcNow;
        var running = await store.CreateAsync(clinic.Id, agent.Id, "scan-ports", null, "running", null, null, now);
        var queued = await store.CreateAsync(clinic.Id, agent.Id, "restart", null, "queued", null, null, now);

        await using var second = await ApiHost.StartAsync(fixture, clock: NewClock());

        foreach (var id in new[] { running.Id, queued.Id })
        {
            var row = (await store.FindAsync(clinic.Id, id))!;
            row.Status.ShouldBe("unknown");
            row.Error.ShouldBe("Máy chủ khởi động lại — không rõ kết quả");
        }
    }
}
