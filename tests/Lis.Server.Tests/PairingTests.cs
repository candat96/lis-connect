using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Agent;
using Lis.Server.Tenancy;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Mã ghép nối dùng một lần và <c>POST /agent/v1/pair</c>: đổi mã lấy khoá, mã dùng lại / hết hạn / sai, ghép nối lại, phòng khám lỗi.</summary>
public sealed class PairingTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> PairAsync(HttpClient client, string? secret, string? machine = null, string? version = null) =>
        client.PostAsJsonAsync("/agent/v1/pair", new { pairingSecret = secret, machineName = machine, agentVersion = version }, Ct);

    private static async Task<string> DetailOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString()!;

    private static async Task<string> AgentKeyOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("agentKey").GetString()!;

    private static async Task<HttpStatusCode> MeStatusAsync(ApiHost host, string key) =>
        (await host.AgentClient(key).GetAsync("/agent/v1/me", Ct)).StatusCode;

    private async Task<(ApiHost Host, FakeTimeProvider Clock, Clinic Clinic)> StartAsync(string code)
    {
        var clock = new FakeTimeProvider(Start);
        var host = await ApiHost.StartAsync(fixture, clock: clock);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử ghép nối");
        return (host, clock, clinic);
    }

    [Fact]
    public async Task Doi_ma_lay_khoa_roi_khoa_goi_duoc_me()
    {
        var (host, _, clinic) = await StartAsync("pkghep1");
        await using var hostScope = host;
        var (agent, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử ghép");

        var response = await PairAsync(host.AgentClient(), ticket.Secret, "MAY-XN-THU", "1.2.3");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("agentId").GetGuid().ShouldBe(agent.Id);
        body.GetProperty("clinicCode").GetString().ShouldBe("pkghep1");
        body.GetProperty("clinicId").GetGuid().ShouldBe(clinic.Id);
        body.GetProperty("clinicName").GetString().ShouldBe("Phòng khám Thử ghép nối");
        body.GetProperty("agentName").GetString().ShouldBe("Agent Thử ghép");
        var key = body.GetProperty("agentKey").GetString()!;

        var me = await new ServerClient(host.AgentClient(key)).MeAsync(Ct);
        me.WorkstationCode.ShouldBe(agent.Id.ToString("N"));
        me.ClinicCode.ShouldBe("pkghep1");

        var stored = (await host.Master.AgentsOfAsync(clinic.Id, host.Clock.GetUtcNow())).Single();
        stored.PairedAt.ShouldBe(Start);
        stored.MachineName.ShouldBe("MAY-XN-THU");
        stored.AgentVersion.ShouldBe("1.2.3");
        stored.PairingExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task Agent_chua_ghep_noi_co_han_ma_va_chua_co_khoa()
    {
        var (host, _, clinic) = await StartAsync("pkghep2");
        await using var hostScope = host;
        var (agent, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử chờ");

        ticket.ExpiresAt.ShouldBe(Start + MasterStore.PairingLifetime);
        var stored = (await host.Master.AgentsOfAsync(clinic.Id, Start)).Single();
        stored.PairedAt.ShouldBeNull();
        stored.PairingExpiresAt.ShouldBe(ticket.ExpiresAt);
        agent.WorkstationCode.ShouldBe(agent.Id.ToString("N"));
        (await host.Master.AuthenticateAsync($"lisa_{agent.Id:N}_{new string('a', 64)}")).ShouldBeNull();
        (await host.Master.AgentsOfAsync(clinic.Id, Start + MasterStore.PairingLifetime)).Single().PairingExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task Ma_dung_lan_hai_thi_410_da_duoc_dung()
    {
        var (host, _, clinic) = await StartAsync("pkghep3");
        await using var hostScope = host;
        var (_, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử hai lần");

        (await PairAsync(host.AgentClient(), ticket.Secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await PairAsync(host.AgentClient(), ticket.Secret);

        second.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await DetailOf(second)).ShouldBe("Mã ghép nối đã được dùng — bấm Ghép nối lại trên web để lấy mã mới");
    }

    [Fact]
    public async Task Hai_yeu_cau_cung_ma_cung_luc_chi_mot_ben_nhan_khoa()
    {
        var (host, _, clinic) = await StartAsync("pkghep4");
        await using var hostScope = host;
        var (_, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử đua");
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var client = host.AgentClient();
            start.Wait(Ct);
            return await PairAsync(client, ticket.Secret, $"MAY-{i}");
        }, Ct)).ToList();
        await Task.Delay(300, Ct);
        start.Set();
        var responses = await Task.WhenAll(tasks);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Gone).ShouldBe(7);
        foreach (var gone in responses.Where(r => r.StatusCode == HttpStatusCode.Gone))
        {
            (await DetailOf(gone)).ShouldContain("đã được dùng");
        }

        // Khoá duy nhất được cấp chạy được, máy trạm ghi đúng một tên máy.
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        (await MeStatusAsync(host, await AgentKeyOf(winner))).ShouldBe(HttpStatusCode.OK);
        (await host.Master.AgentsOfAsync(clinic.Id, Start)).Single().MachineName.ShouldStartWith("MAY-");
    }

    [Fact]
    public async Task Ma_qua_30_phut_thi_410_het_han()
    {
        var (host, clock, clinic) = await StartAsync("pkghep5");
        await using var hostScope = host;
        var (_, late) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử muộn");
        var (_, justInTime) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử kịp");

        clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        (await PairAsync(host.AgentClient(), justInTime.Secret)).StatusCode.ShouldBe(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = await PairAsync(host.AgentClient(), late.Secret);

        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await DetailOf(expired)).ShouldBe("Mã ghép nối đã hết hạn (chỉ dùng trong 30 phút) — bấm Ghép nối lại trên web để lấy mã mới");
    }

    [Fact]
    public async Task Ma_vua_dung_vua_het_han_thi_bao_da_dung()
    {
        var (host, clock, clinic) = await StartAsync("pkghep6");
        await using var hostScope = host;
        var (_, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử cả hai");

        (await PairAsync(host.AgentClient(), ticket.Secret)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromHours(1));

        var again = await PairAsync(host.AgentClient(), ticket.Secret);
        again.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await DetailOf(again)).ShouldContain("đã được dùng");
    }

    [Fact]
    public async Task Ma_sai_thi_401_va_than_thieu_thi_400()
    {
        var (host, _, _) = await StartAsync("pkghep7");
        await using var hostScope = host;
        var client = host.AgentClient();

        var wrong = await PairAsync(client, Lis.Core.Agents.PairingCode.NewSecret());
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DetailOf(wrong)).ShouldBe("Mã ghép nối không đúng — lấy mã mới ở mục Agent trên web");

        foreach (var bad in new string?[] { null, "", "ngan", new string('a', 31), new string('!', 32) })
        {
            var response = await PairAsync(client, bad);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"bí mật: {bad}");
            (await DetailOf(response)).ShouldBe("Mã ghép nối không hợp lệ");
        }

        (await client.PostAsync("/agent/v1/pair", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ghep_noi_lai_khoa_cu_van_chay_toi_khi_ma_moi_duoc_doi()
    {
        var (host, _, clinic) = await StartAsync("pkghep8");
        await using var hostScope = host;
        var (agent, first) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử lại");
        var k1 = await AgentKeyOf(await PairAsync(host.AgentClient(), first.Secret));

        var stale = await host.Master.CreatePairingAsync(clinic.Id, agent.Id, null, host.Clock.GetUtcNow());
        (await MeStatusAsync(host, k1)).ShouldBe(HttpStatusCode.OK);
        var replacement = await host.Master.CreatePairingAsync(clinic.Id, agent.Id, null, host.Clock.GetUtcNow());

        // Mã chưa dùng tạo trước đó đã bị xoá khi cấp mã mới.
        (await PairAsync(host.AgentClient(), stale.Secret)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MeStatusAsync(host, k1)).ShouldBe(HttpStatusCode.OK);

        var k2 = await AgentKeyOf(await PairAsync(host.AgentClient(), replacement.Secret));
        (await MeStatusAsync(host, k1)).ShouldBe(HttpStatusCode.Unauthorized);
        (await MeStatusAsync(host, k2)).ShouldBe(HttpStatusCode.OK);
        (await host.Master.AgentsOfAsync(clinic.Id, Start)).Single().Agent.WorkstationCode.ShouldBe(agent.Id.ToString("N"));
    }

    [Fact]
    public async Task Cap_ma_cho_agent_cua_phong_kham_khac_thi_NotFound()
    {
        var (host, _, clinic) = await StartAsync("pkghep9");
        await using var hostScope = host;
        var other = await host.AddClinicAsync("pkghep9b", "Phòng khám Thử khác");
        var (agent, _) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử riêng");

        var ex = await Should.ThrowAsync<AgentException>(() => host.Master.CreatePairingAsync(other.Id, agent.Id, null, Start));
        ex.Failure.ShouldBe(AgentFailure.NotFound);
    }

    [Fact]
    public async Task Phong_kham_nang_cap_loi_thi_503_va_ma_chua_bi_tieu()
    {
        await using var host = await ApiHost.StartAsync(fixture, clock: new FakeTimeProvider(Start));
        var broken = await host.RegisterUnmigratableClinicAsync("pkghepl", "Phòng khám Thử hỏng");
        var (_, ticket) = await host.Master.CreateAgentAsync(broken.Id, "Agent Thử hỏng", null, Start);
        await host.Provisioner.UpgradeAllAsync();
        host.Provisioner.FailedUpgrades.Keys.ShouldContain(broken.Id);

        var response = await PairAsync(host.AgentClient(), ticket.Secret);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DetailOf(response)).ShouldBe("Cơ sở dữ liệu của phòng khám chưa nâng cấp được — báo quản trị hệ thống");
        await using var connection = new NpgsqlConnection(fixture.MasterConnection);
        (await connection.QuerySingleAsync<int>(
            "SELECT count(*) FROM agent_pairing p JOIN agent a ON a.id = p.agent_id WHERE a.clinic_id = @id AND p.used_at IS NULL",
            new { id = broken.Id })).ShouldBe(1);
    }

    [Fact]
    public async Task Xoa_agent_thi_ma_cua_no_thanh_401()
    {
        var (host, _, clinic) = await StartAsync("pkghepx");
        await using var hostScope = host;
        var (agent, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử xoá");

        (await host.Master.DeleteAgentAsync(clinic.Id, agent.Id)).ShouldBeTrue();

        (await PairAsync(host.AgentClient(), ticket.Secret)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_luu_phien_ban_va_ten_may()
    {
        var (host, _, clinic) = await StartAsync("pkghepy");
        await using var hostScope = host;
        var (_, ticket) = await host.CreateAgentWithPairingAsync(clinic, "Agent Thử trạng thái");
        var key = await AgentKeyOf(await PairAsync(host.AgentClient(), ticket.Secret, "MAY-CU", "0.9"));
        var client = host.AgentClient(key);

        (await client.PostAsJsonAsync("/agent/v1/status", new { instruments = Array.Empty<object>(), agentVersion = "2.0.1", machineName = "MAY-MOI" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = (await host.Master.AgentsOfAsync(clinic.Id, Start)).Single();
        after.AgentVersion.ShouldBe("2.0.1");
        after.MachineName.ShouldBe("MAY-MOI");

        (await client.PostAsJsonAsync("/agent/v1/status", new { instruments = Array.Empty<object>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var kept = (await host.Master.AgentsOfAsync(clinic.Id, Start)).Single();
        kept.AgentVersion.ShouldBe("2.0.1");
        kept.MachineName.ShouldBe("MAY-MOI");
    }
}
