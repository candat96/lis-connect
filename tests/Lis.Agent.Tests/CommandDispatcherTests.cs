using System.Text.Json;
using Lis.Connections;
using Lis.Core.Agents;
using Lis.Data.Instruments;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Lis.Agent.Tests;

public sealed class CommandDispatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-agent-cmd-" + Guid.NewGuid().ToString("N"));

    public CommandDispatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private static ChannelEnvelope Command(string name, JsonElement? args = null) =>
        new(ChannelProtocol.Command, Guid.NewGuid(), name, args);

    private sealed class ThrowingHandler(Exception ex) : IAgentCommandHandler
    {
        public string Name => "x";

        public Task<JsonElement?> RunAsync(JsonElement? args, CancellationToken ct) => throw ex;
    }

    [Fact]
    public async Task Lenh_la_tra_ok_false_dung_cau()
    {
        var dispatcher = new CommandDispatcher([]);
        var result = await dispatcher.RunAsync(Command("bay-ba"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        result.Ok.ShouldBe(false);
        result.Error.ShouldBe("Agent chưa hỗ trợ lệnh bay-ba");
    }

    [Fact]
    public async Task Loi_da_biet_va_ngoai_le_la_chi_gui_message()
    {
        var known = await new CommandDispatcher([new ThrowingHandler(new AgentCommandException("Cổng bận"))])
            .RunAsync(Command("x"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        known.Ok.ShouldBe(false);
        known.Error.ShouldBe("Cổng bận");

        var unexpected = await new CommandDispatcher([new ThrowingHandler(new InvalidOperationException("hỏng thử"))])
            .RunAsync(Command("x"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        unexpected.Ok.ShouldBe(false);
        unexpected.Error.ShouldBe("hỏng thử");
    }

    [Fact]
    public async Task Started_gui_truoc_result_va_trung_id()
    {
        var order = new List<string>();
        var dispatcher = new CommandDispatcher([new RecordingHandler("x", order)]);
        var command = Command("x");
        var result = await dispatcher.RunAsync(command, e =>
        {
            order.Add("gửi " + e.Type);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        order.ShouldBe(["gửi started", "bắt đầu ", "xong "]);
        result.Type.ShouldBe(ChannelProtocol.Result);
        result.Id.ShouldBe(command.Id);
        result.Ok.ShouldBe(true);
    }

    [Fact]
    public async Task Gui_started_loi_van_chay_lenh()
    {
        var order = new List<string>();
        var result = await new CommandDispatcher([new RecordingHandler("x", order)])
            .RunAsync(Command("x"), _ => throw new InvalidOperationException("đứt"), TestContext.Current.CancellationToken);
        result.Ok.ShouldBe(true);
        order.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Hai_lenh_cung_luc_chay_tuan_tu()
    {
        var order = new List<string>();
        var gate = new TaskCompletionSource();
        var dispatcher = new CommandDispatcher([new RecordingHandler("x", order, () => gate.Task)]);

        var first = dispatcher.RunAsync(Command("x", JsonDocument.Parse("1").RootElement), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await Polling.UntilAsync(() => order.Count == 1, "lệnh 1 bắt đầu");
        var second = dispatcher.RunAsync(Command("x", JsonDocument.Parse("2").RootElement), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // lệnh 2 phải vẫn chờ — không bắt đầu chồng
        order.ShouldBe(["bắt đầu 1"]);

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Polling.Wait, TestContext.Current.CancellationToken);
        order.ShouldBe(["bắt đầu 1", "xong 1", "bắt đầu 2", "xong 2"]);
    }

    [Fact]
    public async Task Revoke_xoa_khoa_giu_hang_cho()
    {
        var paths = new AgentPaths(_root);
        File.WriteAllText(paths.Pairing, "{}");
        File.WriteAllText(paths.IdentityCache, "{}");
        File.WriteAllText(paths.InstrumentsCache, "[]");
        File.WriteAllText(paths.DevKey, "k");
        Directory.CreateDirectory(paths.Pending);
        File.WriteAllText(Path.Combine(paths.Pending, "x.json"), "{}");
        var manager = new FakeConnections();
        var runtime = new AgentRuntime { Manager = manager };
        var lifetime = new FakeLifetime();
        var service = new FakeServiceControl(exists: true, running: true);
        var dispatcher = new CommandDispatcher([new RevokeHandler(runtime, paths, lifetime, service, runningAsService: true, NullLogger<RevokeHandler>.Instance)]);

        var command = Command("revoke");
        var sent = new List<string>();
        var result = await dispatcher.RunAsync(command, e => { sent.Add(e.Type); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        // Kết quả có trước, chưa đụng tới tệp / máy.
        result.Ok.ShouldBe(true);
        File.Exists(paths.Pairing).ShouldBeTrue();
        manager.StopAlls.ShouldBe(0);

        await dispatcher.AfterResultAsync(command, TestContext.Current.CancellationToken);

        File.Exists(paths.Pairing).ShouldBeFalse();
        File.Exists(paths.IdentityCache).ShouldBeFalse();
        File.Exists(paths.InstrumentsCache).ShouldBeFalse();
        File.Exists(paths.DevKey).ShouldBeFalse();
        File.Exists(Path.Combine(paths.Pending, "x.json")).ShouldBeTrue();
        manager.StopAlls.ShouldBe(1);
        lifetime.Revokes.ShouldBe(1);
        service.Commands.ShouldContain("Disable");
        runtime.Revoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_khong_chay_dich_vu_thi_khong_tat_dich_vu()
    {
        var paths = new AgentPaths(_root);
        var service = new FakeServiceControl(exists: true, running: true);
        var lifetime = new FakeLifetime();
        var handler = new RevokeHandler(new AgentRuntime(), paths, lifetime, service, runningAsService: false, NullLogger<RevokeHandler>.Instance);
        await handler.AfterResultAsync(TestContext.Current.CancellationToken);
        service.Commands.ShouldNotContain("Disable");
        lifetime.Revokes.ShouldBe(1);
    }

    private sealed class OrderedConnections(List<string> log) : IInstrumentConnections
    {
        public event Action<InstrumentConnectionStatus>? StatusChanged { add { } remove { } }

        public InstrumentConnectionStatus StatusOf(Guid id) => new(id, LinkState.NotRunning, "giả");

        public Task ReloadAsync() => Task.CompletedTask;

        public Task RestartAsync(Guid id) => Task.CompletedTask;

        public async Task StopAllAsync()
        {
            await Task.Delay(50);
            log.Add("StopAll xong");
        }

        public Task<int> RetryPendingNowAsync() => Task.FromResult(0);

        public Task<PortLease> BorrowPortAsync(string portName, CancellationToken ct = default) => Task.FromResult(new PortLease([]));
    }

    private sealed class OrderedLifetime(List<string> log) : IAgentLifetime
    {
        public void RestartSoon() => log.Add("RestartSoon");

        public void StopForRevoke() => log.Add("StopForRevoke");
    }

    [Fact]
    public async Task Restart_dich_vu_tra_ket_qua_roi_dong_may_xong_moi_khoi_dong_lai()
    {
        var log = new List<string>();
        var runtime = new AgentRuntime { Manager = new OrderedConnections(log) };
        var dispatcher = new CommandDispatcher([new RestartHandler(runtime, new OrderedLifetime(log), runningAsService: true)]);
        var command = Command("restart");

        (await dispatcher.RunAsync(command, _ => Task.CompletedTask, TestContext.Current.CancellationToken)).Ok.ShouldBe(true);
        log.ShouldBeEmpty();
        await dispatcher.AfterResultAsync(command, TestContext.Current.CancellationToken);

        log.ShouldBe(["StopAll xong", "RestartSoon"]);
    }

    [Fact]
    public async Task Restart_khong_phai_dich_vu_bao_loi_va_khong_thoat()
    {
        var lifetime = new FakeLifetime();
        var dispatcher = new CommandDispatcher([new RestartHandler(new AgentRuntime(), lifetime, runningAsService: false)]);
        var result = await dispatcher.RunAsync(Command("restart"), _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        result.Ok.ShouldBe(false);
        result.Error.ShouldBe("Agent không chạy như dịch vụ nên không tự khởi động lại");
        lifetime.Restarts.ShouldBe(0); // ok=false nên ChannelClient không gọi AfterResult
    }

    private static InstrumentInfo Instrument(Guid id, string workstation) =>
        new(id, "Máy Thử", "AU480", "beckman-au", null, null, workstation, true);

    [Fact]
    public async Task Lenh_nap_lai_noi_lai_gui_bu()
    {
        var mine = Guid.NewGuid();
        var manager = new FakeConnections { Remaining = 3 };
        var runtime = new AgentRuntime
        {
            Manager = manager,
            WorkstationCode = "XN-01",
            Instruments = [Instrument(mine, "XN-01"), Instrument(Guid.NewGuid(), "XN-02")],
        };

        var reload = await new ReloadConfigHandler(runtime).RunAsync(null, TestContext.Current.CancellationToken);
        reload!.Value.GetProperty("instruments").GetInt32().ShouldBe(1);
        manager.Reloads.ShouldBe(1);

        var flush = await new FlushPendingHandler(runtime).RunAsync(null, TestContext.Current.CancellationToken);
        flush!.Value.GetProperty("remaining").GetInt32().ShouldBe(3);

        var reconnect = new ReconnectInstrumentHandler(runtime);
        await reconnect.RunAsync(JsonDocument.Parse($$"""{"instrumentId":"{{mine}}"}""").RootElement, TestContext.Current.CancellationToken);
        manager.Restarts.ShouldBe([mine]);

        var foreign = await Should.ThrowAsync<AgentCommandException>(() =>
            reconnect.RunAsync(JsonDocument.Parse($$"""{"instrumentId":"{{Guid.NewGuid()}}"}""").RootElement, TestContext.Current.CancellationToken));
        foreign.Message.ShouldBe("Máy này không chạy trên agent");
        manager.Restarts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Chua_co_manager_thi_bao_chua_san_sang()
    {
        var ex = await Should.ThrowAsync<AgentCommandException>(() => new ReloadConfigHandler(new AgentRuntime()).RunAsync(null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("chưa sẵn sàng");
    }
}
