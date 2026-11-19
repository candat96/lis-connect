using System.Text.Json;
using Dapper;
using Lis.Server.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>
/// Bảng lệnh agent trên CSDL master: vòng đời lệnh, hết hạn hàng chờ, quá hạn khi đang chạy, mất kết nối và khởi động lại server,
/// lịch sử theo phòng khám, thu hồi khoá và lưu cổng COM lần quét cuối. Mọi mốc thời gian đều là tham số <c>now</c> truyền vào.
/// </summary>
public sealed class AgentCommandStoreTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    private sealed record Env(
        MasterStore Master, AgentCommandStore Commands, Clinic ClinicA, AgentInfo AgentA, Clinic ClinicB, AgentInfo AgentB);

    private string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(fixture.MasterConnection) { Database = database }.ConnectionString;

    /// <summary>CSDL master riêng cho mỗi test (đã nâng cấp migration), hai phòng khám, mỗi phòng một agent.</summary>
    private async Task<Env> SetupAsync()
    {
        var db = $"lis_master_lenh_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionTo("postgres")))
        {
            await admin.OpenAsync(Ct);
            await admin.ExecuteAsync($"CREATE DATABASE \"{db}\"");
        }

        var master = new MasterStore(ConnectionTo(db));
        await master.EnsureSchemaAsync();
        var clinicA = await master.AddClinicAsync("lenh-thu-a", "Phòng khám Thử lệnh A", "clinic_thu_lenh_a");
        var clinicB = await master.AddClinicAsync("lenh-thu-b", "Phòng khám Thử lệnh B", "clinic_thu_lenh_b");
        var (agentA, _) = await master.CreateAgentAsync(clinicA.Id, "Agent Thử A", null, Start);
        var (agentB, _) = await master.CreateAgentAsync(clinicB.Id, "Agent Thử B", null, Start);
        return new Env(master, new AgentCommandStore(ConnectionTo(db)), clinicA, agentA, clinicB, agentB);
    }

    private static Task<AgentCommandRow> QueueAsync(Env env, string name, DateTimeOffset at, string? args = null) =>
        env.Commands.CreateAsync(env.ClinicA.Id, env.AgentA.Id, name, args, "queued", null, "Thử người dùng", at);

    private static async Task<AgentCommandRow> FindAsync(Env env, Guid id) =>
        (await env.Commands.FindAsync(env.ClinicA.Id, id))!;

    [Fact]
    public async Task Tao_roi_chuyen_trang_thai_ghi_moc_thoi_gian()
    {
        var env = await SetupAsync();
        var cmd = await QueueAsync(env, "scan-ports", Start);
        cmd.Status.ShouldBe("queued");
        cmd.StartedAt.ShouldBeNull();

        (await env.Commands.TransitionAsync(cmd.Id, ["queued"], "sent", Start.AddSeconds(1))).ShouldBeTrue();
        var sent = await FindAsync(env, cmd.Id);
        sent.Status.ShouldBe("sent");
        sent.StartedAt.ShouldBe(Start.AddSeconds(1));

        (await env.Commands.TransitionAsync(cmd.Id, ["sent"], "done", Start.AddSeconds(2), "{\"ok\":1}")).ShouldBeTrue();
        var done = await FindAsync(env, cmd.Id);
        done.Status.ShouldBe("done");
        done.FinishedAt.ShouldBe(Start.AddSeconds(2));
        done.Result!.Value.GetProperty("ok").GetInt32().ShouldBe(1);

        (await env.Commands.TransitionAsync(cmd.Id, ["queued"], "sent", Start.AddSeconds(3))).ShouldBeFalse();
        (await FindAsync(env, cmd.Id)).Status.ShouldBe("done");
    }

    [Fact]
    public async Task Het_han_hang_cho_sau_60_giay()
    {
        var env = await SetupAsync();
        var cmd = await QueueAsync(env, "scan-ports", Start);

        // olderThan = now - 60 giây: lệnh xếp hàng từ mốc đó trở về trước là hết hạn.
        var before = Start.AddSeconds(60).AddMilliseconds(-1);
        (await env.Commands.ExpireQueuedAsync(before.AddSeconds(-60), before)).ShouldBe(0);
        (await FindAsync(env, cmd.Id)).Status.ShouldBe("queued");

        var after = Start.AddSeconds(60).AddMilliseconds(1);
        (await env.Commands.ExpireQueuedAsync(after.AddSeconds(-60), after)).ShouldBe(1);
        var expired = await FindAsync(env, cmd.Id);
        expired.Status.ShouldBe("expired");
        expired.Error.ShouldBe("Agent không kết nối trong 60 giây");
        expired.FinishedAt.ShouldBe(after);
    }

    [Fact]
    public async Task Qua_han_chay_thanh_unknown_theo_loai_lenh()
    {
        var env = await SetupAsync();
        var port = await QueueAsync(env, "test-port", Start, "{\"seconds\":10}");
        (await env.Commands.TransitionAsync(port.Id, ["queued"], "running", Start)).ShouldBeTrue();

        (await env.Commands.TimeoutRunningAsync(Start.AddSeconds(39))).ShouldBe(0);
        (await FindAsync(env, port.Id)).Status.ShouldBe("running");

        (await env.Commands.TimeoutRunningAsync(Start.AddSeconds(41))).ShouldBe(1);
        var timedOut = await FindAsync(env, port.Id);
        timedOut.Status.ShouldBe("unknown");
        timedOut.Error.ShouldBe("Agent không trả lời");
    }

    [Fact]
    public async Task Lenh_update_chay_toi_10_phut_con_lenh_khac_2_phut()
    {
        var env = await SetupAsync();
        var scan = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(scan.Id, ["queued"], "running", Start)).ShouldBeTrue();
        var upgrade = await QueueAsync(env, "update", Start);
        (await env.Commands.TransitionAsync(upgrade.Id, ["queued"], "running", Start)).ShouldBeTrue();

        // Lúc 9 phút: scan-ports đã quá 2 phút, update còn trong 10 phút.
        (await env.Commands.TimeoutRunningAsync(Start.AddMinutes(9))).ShouldBe(1);
        (await FindAsync(env, scan.Id)).Status.ShouldBe("unknown");
        (await FindAsync(env, upgrade.Id)).Status.ShouldBe("running");

        (await env.Commands.TimeoutRunningAsync(Start.AddMinutes(11))).ShouldBe(1);
        (await FindAsync(env, upgrade.Id)).Status.ShouldBe("unknown");
    }

    [Fact]
    public async Task Mat_ket_noi_khong_dong_lenh_update()
    {
        var env = await SetupAsync();
        var scan = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(scan.Id, ["queued"], "running", Start)).ShouldBeTrue();
        var upgrade = await QueueAsync(env, "update", Start);
        (await env.Commands.TransitionAsync(upgrade.Id, ["queued"], "running", Start)).ShouldBeTrue();

        (await env.Commands.MarkDisconnectedAsync(env.AgentA.Id, Start.AddSeconds(5))).ShouldBe(1);

        var lost = await FindAsync(env, scan.Id);
        lost.Status.ShouldBe("unknown");
        lost.Error.ShouldBe("Agent mất kết nối giữa chừng");
        (await FindAsync(env, upgrade.Id)).Status.ShouldBe("running");
    }

    [Fact]
    public async Task Khoi_dong_lai_server_dua_lenh_dang_do_ve_unknown()
    {
        var env = await SetupAsync();
        var queued = await QueueAsync(env, "scan-ports", Start);
        var sent = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(sent.Id, ["queued"], "sent", Start)).ShouldBeTrue();
        var running = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(running.Id, ["queued"], "running", Start)).ShouldBeTrue();
        var finished = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(finished.Id, ["queued"], "sent", Start)).ShouldBeTrue();
        (await env.Commands.TransitionAsync(finished.Id, ["sent"], "done", Start)).ShouldBeTrue();

        (await env.Commands.MarkUnfinishedUnknownAsync(Start.AddSeconds(10))).ShouldBe(3);

        foreach (var id in new[] { queued.Id, sent.Id, running.Id })
        {
            var row = await FindAsync(env, id);
            row.Status.ShouldBe("unknown");
            row.Error.ShouldBe("Máy chủ khởi động lại — không rõ kết quả");
        }
        (await FindAsync(env, finished.Id)).Status.ShouldBe("done");
    }

    [Fact]
    public async Task Lich_su_chi_cua_phong_kham()
    {
        var env = await SetupAsync();
        var cmd = await QueueAsync(env, "scan-ports", Start);

        (await env.Commands.ListAsync(env.ClinicB.Id, env.AgentA.Id, 20)).ShouldBeEmpty();
        (await env.Commands.FindAsync(env.ClinicB.Id, cmd.Id)).ShouldBeNull();
        (await env.Commands.ListAsync(env.ClinicA.Id, env.AgentA.Id, 20)).Select(c => c.Id).ShouldBe([cmd.Id]);
        (await env.Commands.ListAsync(env.ClinicA.Id, env.AgentA.Id, 0)).Count.ShouldBe(1);
        (await env.Commands.ListAsync(env.ClinicA.Id, env.AgentA.Id, 500)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Tham_so_seconds_hong_khong_lam_hong_quet()
    {
        var env = await SetupAsync();
        // test-port có seconds không phải số: phải coi như 0 và quá hạn sau 30 giây, không được làm hỏng cả lần quét.
        var port = await QueueAsync(env, "test-port", Start, "{\"seconds\":\"abc\"}");
        (await env.Commands.TransitionAsync(port.Id, ["queued"], "running", Start)).ShouldBeTrue();
        var scan = await QueueAsync(env, "scan-ports", Start);
        (await env.Commands.TransitionAsync(scan.Id, ["queued"], "running", Start)).ShouldBeTrue();

        (await env.Commands.TimeoutRunningAsync(Start.AddSeconds(29))).ShouldBe(0);
        (await env.Commands.TimeoutRunningAsync(Start.AddSeconds(31))).ShouldBe(1);
        (await FindAsync(env, port.Id)).Status.ShouldBe("unknown");
        (await FindAsync(env, scan.Id)).Status.ShouldBe("running");

        (await env.Commands.TimeoutRunningAsync(Start.AddMinutes(2).AddSeconds(1))).ShouldBe(1);
        (await FindAsync(env, scan.Id)).Status.ShouldBe("unknown");
    }

    [Fact]
    public async Task Lenh_update_mo_trong_30_phut()
    {
        var env = await SetupAsync();
        var upgrade = await QueueAsync(env, "update", Start);
        (await env.Commands.TransitionAsync(upgrade.Id, ["queued"], "sent", Start)).ShouldBeTrue();

        (await env.Commands.OpenUpdateAsync(env.AgentA.Id, Start.AddMinutes(29)))!.Id.ShouldBe(upgrade.Id);
        (await env.Commands.OpenUpdateAsync(env.AgentA.Id, Start.AddMinutes(31))).ShouldBeNull();

        (await env.Commands.TransitionAsync(upgrade.Id, ["sent"], "done", Start.AddMinutes(1))).ShouldBeTrue();
        (await env.Commands.OpenUpdateAsync(env.AgentA.Id, Start.AddMinutes(2))).ShouldBeNull();
    }

    [Fact]
    public async Task Thu_hoi_xoa_khoa_va_ma_ghep_noi_chua_dung()
    {
        var env = await SetupAsync();
        var (agent, ticket) = await env.Master.CreateAgentAsync(env.ClinicA.Id, "Agent Thử thu hồi", null, Start);
        var redeemed = await env.Master.RedeemPairingAsync(ticket.Secret, "MAY-THU", "1.0", Start);
        var key = redeemed.AgentKey!;
        (await env.Master.AuthenticateAsync(key)).ShouldNotBeNull();

        var pending = await env.Master.CreatePairingAsync(env.ClinicA.Id, agent.Id, null, Start);

        (await env.Master.RevokeAgentAsync(env.ClinicA.Id, agent.Id)).ShouldBeTrue();

        (await env.Master.AuthenticateAsync(key)).ShouldBeNull();
        (await env.Master.AgentsOfAsync(env.ClinicA.Id, Start)).Single(a => a.Agent.Id == agent.Id).PairedAt.ShouldBeNull();
        var reuse = await env.Master.RedeemPairingAsync(pending.Secret, "MAY-THU", "1.0", Start);
        reuse.Identity.ShouldBeNull();
        reuse.Failure.ShouldNotBeNull();

        (await env.Master.RevokeAgentAsync(env.ClinicB.Id, agent.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Luu_cong_com_lan_quet_cuoi()
    {
        var env = await SetupAsync();

        await env.Master.SavePortsAsync(env.AgentA.Id, "[\"COM3\",\"COM5\"]", Start.AddSeconds(5));

        var agent = (await env.Master.AgentsOfAsync(env.ClinicA.Id, Start)).Single();
        JsonSerializer.Deserialize<string[]>(agent.LastPortsJson!).ShouldBe(["COM3", "COM5"]);
        agent.LastPortsAt.ShouldBe(Start.AddSeconds(5));
        (await env.Master.AgentsOfAsync(env.ClinicB.Id, Start)).Single().LastPortsJson.ShouldBeNull();
    }
}
