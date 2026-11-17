using Shouldly;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Core.Agents;
using Lis.Server.Tenancy;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Lis.Server.Tests.Api;

public sealed class AgentsEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string AdminOnly = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";
    private const string LabStaffOnly = "Tài khoản không có quyền xem mục này";
    private const string BadUuid = "Validation failed (uuid is expected)";
    private const string NotFound = "Không tìm thấy agent";
    private const string Generic = "Dữ liệu gửi lên không hợp lệ";
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldBe(message);
    }

    private sealed record Setup(ApiHost Host, Clinic Clinic, HttpClient Admin, HttpClient Tech, HttpClient Reception, string Connection) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public async Task<T> Scalar<T>(string sql, object? args = null)
        {
            await using var connection = new NpgsqlConnection(Connection);
            return (await connection.ExecuteScalarAsync<T>(sql, args))!;
        }

        public async Task Exec(string sql, object? args = null)
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.ExecuteAsync(sql, args);
        }

        public Task<Guid> Instrument(string name, string? workstation) =>
            Scalar<Guid>("INSERT INTO instrument (name, workstation_code) VALUES (@name, @workstation) RETURNING id", new { name, workstation });

        /// <summary>Thêm một dòng log; trả về id.</summary>
        public Task<long> Log(Guid agent, Guid? instrument, string direction, string? hex = null, string? note = null, DateTimeOffset? at = null) =>
            Scalar<long>(
                "INSERT INTO agent_log (agent_id, instrument_id, at, direction, hex, note) VALUES (@agent, @instrument, @at, @direction, @hex, @note) RETURNING id",
                new { agent, instrument, at = at ?? DateTimeOffset.UtcNow, direction, hex, note });
    }

    /// <param name="clock">Đồng hồ của server thử (mặc định đồng hồ hệ thống) — test cần đo hạn mã thì truyền đồng hồ giả.</param>
    private async Task<Setup> StartAsync(string code, IReadOnlyDictionary<string, string>? settings = null, TimeProvider? clock = null)
    {
        var host = await ApiHost.StartAsync(fixture, settings: settings, clock: clock);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        await host.AddUserAsync(clinic, "tn", Password, "tiep-nhan");
        var connection = fixture.TenantTemplate.Replace("{db}", $"lis_{code}", StringComparison.Ordinal);
        return new Setup(
            host, clinic, await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password),
            await host.LoginAsync(code, "tn", Password), connection);
    }

    private static object Body(string name = "Agent Thử") => new { name };

    /// <summary>Tạo agent (201): trả về id và mã ghép nối dùng một lần.</summary>
    private static async Task<(Guid Id, string Code)> Create(HttpClient client, string name = "Agent Thử")
    {
        var response = await client.PostAsJsonAsync("/api/agents", Body(name), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await JsonOf(response);
        return (Guid.Parse(body.GetProperty("id").GetString()!), body.GetProperty("pairing").GetProperty("code").GetString()!);
    }

    /// <summary>Đổi mã ghép nối lấy khoá qua <c>POST /agent/v1/pair</c>, đúng như agent cài đặt làm.</summary>
    private static async Task<string> Pair(Setup s, string code, string machine = "MAY-THU", string version = "1.0.0")
    {
        PairingCode.TryFind(code, out var parsed).ShouldBeTrue();
        var response = await s.Host.AgentClient().PostAsJsonAsync(
            "/agent/v1/pair", new { pairingSecret = parsed!.Secret, machineName = machine, agentVersion = version }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await JsonOf(response)).GetProperty("agentKey").GetString()!;
    }

    private static async Task<JsonElement> List(HttpClient client)
    {
        var response = await client.GetAsync("/api/agents", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonOf(response);
    }

    private static async Task<JsonElement> One(HttpClient client, Guid id) =>
        (await List(client)).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id.ToString());

    /// <summary>Gọi <c>GET /agent/v1/me</c> bằng khoá agent.</summary>
    private static async Task<HttpResponseMessage> Me(Setup s, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/agent/v1/me");
        request.Headers.Add("X-Agent-Key", key);
        return await s.Host.Anonymous.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Logs(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/agent-logs" + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonOf(response);
    }

    private static string[] Ids(JsonElement logs) => logs.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task Chua_dang_nhap_thi_401_o_moi_route()
    {
        await using var s = await StartAsync("pkg401");
        var id = Guid.NewGuid();
        var anonymous = s.Host.Anonymous;

        (await anonymous.GetAsync("/api/agents", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/agents", Body(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PatchAsJsonAsync($"/api/agents/{id}", Body(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/agents/{id}/pairing", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/agents/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/agent-logs", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ky_thuat_vien_xem_duoc_nhung_ghi_thi_403_va_tiep_nhan_khong_xem_duoc()
    {
        await using var s = await StartAsync("pkg403");
        var (id, _) = await Create(s.Admin);

        (await s.Tech.GetAsync("/api/agents", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Tech.GetAsync("/api/agent-logs", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/agents", Body("Agent khác Thử"), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PatchAsJsonAsync($"/api/agents/{id}", Body("Tên khác Thử"), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PostAsync($"/api/agents/{id}/pairing", null, Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.DeleteAsync($"/api/agents/{id}", Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Reception.GetAsync("/api/agents", Ct), HttpStatusCode.Forbidden, LabStaffOnly);
        await ShouldFail(await s.Reception.GetAsync("/api/agent-logs", Ct), HttpStatusCode.Forbidden, LabStaffOnly);
        // Kiểm đầu vào chạy trước kiểm quyền (pipe trước handler); kiểm quyền trước kiểm tra tồn tại.
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/agents", Body(""), Ct), HttpStatusCode.BadRequest, "Nhập tên agent");
        await ShouldFail(await s.Tech.DeleteAsync("/api/agents/khong-phai-uuid", Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Tech.PostAsync($"/api/agents/{Guid.NewGuid()}/pairing", null, Ct), HttpStatusCode.Forbidden, AdminOnly);
        // Lọc log sai: quyền kiểm trước.
        await ShouldFail(await s.Reception.GetAsync("/api/agent-logs?agentId=sai", Ct), HttpStatusCode.Forbidden, LabStaffOnly);
        (await s.Admin.GetAsync($"/api/agents/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Tao_agent_tra_ma_ghep_noi_doi_duoc_lay_khoa()
    {
        await using var s = await StartAsync("pkgcreate", clock: new FakeTimeProvider(Start));

        var response = await s.Admin.PostAsJsonAsync("/api/agents", Body("  Agent Thử  "), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await JsonOf(response);
        var id = Guid.Parse(body.GetProperty("id").GetString()!);
        var pairing = body.GetProperty("pairing");
        var code = pairing.GetProperty("code").GetString()!;
        PairingCode.TryFind(code, out var parsed).ShouldBeTrue();
        parsed!.ServerUrl.ShouldBe("http://localhost");
        parsed.ClinicCode.ShouldBe("pkgcreate");
        pairing.GetProperty("expiresAt").GetString().ShouldBe("2026-11-17T03:30:00.000Z");

        var key = await Pair(s, code);
        var me = await Me(s, key);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var identity = await JsonOf(me);
        identity.GetProperty("agentName").GetString().ShouldBe("Agent Thử");
        identity.GetProperty("clinicCode").GetString().ShouldBe("pkgcreate");
        var listed = await One(s.Tech, id);
        listed.GetProperty("name").GetString().ShouldBe("Agent Thử");
        listed.GetProperty("online").GetBoolean().ShouldBeFalse();
        listed.GetProperty("lastSeenAt").ValueKind.ShouldBe(JsonValueKind.Null);
        listed.GetProperty("instruments").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Tao_agent_kiem_dau_vao_dung_cau_bao_loi_va_khong_nhan_ma_may_tram()
    {
        await using var s = await StartAsync("pkgvalid");
        await Create(s.Admin, "Agent Thử");

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/agents", Body(""), Ct), HttpStatusCode.BadRequest, "Nhập tên agent");
        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/agents", Body("   "), Ct), HttpStatusCode.BadRequest, "Nhập tên agent");
        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/agents", Body(new string('a', 101)), Ct), HttpStatusCode.BadRequest, Generic);
        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/agents", new { }, Ct), HttpStatusCode.BadRequest, Generic);
        // Mã máy trạm không còn do người dùng nhập: gửi lên là trường lạ → 400.
        (await s.Admin.PostAsJsonAsync("/api/agents", new { name = "Agent Thử", workstationCode = "XN-02" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Admin.PostAsJsonAsync("/api/agents", new { name = "Agent Thử", la = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // Tên dài đúng 100 ký tự thì được.
        await Create(s.Admin, new string('b', 100));
        (await List(s.Admin)).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Ghep_noi_lai_201_ma_moi_khoa_cu_chay_toi_khi_doi_ma_va_agent_phong_kham_khac_404()
    {
        await using var s = await StartAsync("pkgrepair");
        var (id, code) = await Create(s.Admin);
        var first = await Pair(s, code);

        var response = await s.Admin.PostAsync($"/api/agents/{id}/pairing", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var fresh = await JsonOf(response);
        var newCode = fresh.GetProperty("code").GetString()!;
        newCode.ShouldNotBe(code);
        fresh.GetProperty("expiresAt").GetString().ShouldEndWith("Z");
        PairingCode.TryFind(newCode, out var parsed).ShouldBeTrue();
        parsed!.ClinicCode.ShouldBe("pkgrepair");
        // Mã mới chưa đổi thì khoá cũ vẫn chạy.
        (await Me(s, first)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await Pair(s, newCode);
        (await Me(s, first)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Me(s, second)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ShouldFail(await s.Admin.PostAsync("/api/agents/xyz/pairing", null, Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.PostAsync($"/api/agents/{Guid.NewGuid()}/pairing", null, Ct), HttpStatusCode.NotFound, NotFound);
        var otherClinic = await s.Host.AddClinicAsync("pkgrepairx", "Phòng khám khác Thử");
        var foreign = await s.Host.AddAgentAsync(otherClinic, "Agent ngoài Thử", "XN-77");
        await ShouldFail(await s.Admin.PostAsync($"/api/agents/{foreign.Id}/pairing", null, Ct), HttpStatusCode.NotFound, NotFound);
    }

    [Fact]
    public async Task Danh_sach_co_trang_thai_ghep_noi()
    {
        await using var s = await StartAsync("pkgstatus", clock: new FakeTimeProvider(Start));
        var (id, code) = await Create(s.Admin, "Agent Thử");

        var waiting = await One(s.Tech, id);
        waiting.GetProperty("paired").GetBoolean().ShouldBeFalse();
        waiting.GetProperty("pairedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        waiting.GetProperty("machineName").ValueKind.ShouldBe(JsonValueKind.Null);
        waiting.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Null);
        waiting.GetProperty("pairingExpiresAt").GetString().ShouldBe("2026-11-17T03:30:00.000Z");
        waiting.GetProperty("workstationCode").GetString().ShouldBe(id.ToString("N"));

        await Pair(s, code, "MAY-XN-01", "1.2.3");

        var paired = await One(s.Tech, id);
        paired.GetProperty("paired").GetBoolean().ShouldBeTrue();
        paired.GetProperty("pairedAt").GetString().ShouldBe("2026-11-17T03:00:00.000Z");
        paired.GetProperty("machineName").GetString().ShouldBe("MAY-XN-01");
        paired.GetProperty("version").GetString().ShouldBe("1.2.3");
        paired.GetProperty("pairingExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task PublicUrl_dat_thi_ma_dung_dia_chi_do()
    {
        await using var s = await StartAsync("pkgpublic", settings: new Dictionary<string, string> { ["PublicUrl"] = "https://lis.thu.vn" });
        var (_, code) = await Create(s.Admin);

        PairingCode.TryFind(code, out var parsed).ShouldBeTrue();
        parsed!.ServerUrl.ShouldBe("https://lis.thu.vn");
    }

    [Fact]
    public void PublicUrl_sai_thi_server_khong_khoi_dong()
    {
        static void Make(string publicUrl)
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
            configuration["Jwt:Secret"] = ApiHost.TestSecret;
            configuration["PublicUrl"] = publicUrl;
            ServerOptions.From(configuration);
        }

        Should.Throw<InvalidOperationException>(() => Make("lis.thu.vn"));
        Should.Throw<InvalidOperationException>(() => Make("https://lis.thu.vn/?ma=1"));
        Should.Throw<InvalidOperationException>(() => Make("ftp://lis.thu.vn"));
        Should.NotThrow(() => Make("https://lis.thu.vn/"));
    }

    [Fact]
    public async Task Danh_sach_gom_may_theo_ma_may_tram_khong_phan_biet_hoa_thuong_kem_trang_thai_khi_online()
    {
        await using var s = await StartAsync("pkglist");
        var agent = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm Thử", "XN-01");
        var idle = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm khác Thử", "XN-02");
        var one = await s.Instrument("Máy A Thử", "xn-01");
        var two = await s.Instrument("Máy B Thử", "XN-01");
        await s.Instrument("Máy không gán Thử", null);
        var elsewhere = await s.Instrument("Máy nơi khác Thử", "XN-99");
        await s.Host.TouchAgentAsync(agent, $$"""[{"instrumentId":"{{one}}","state":"Connected","text":"Đã nối","lastResultAt":null,"samplesReceived":3,"pendingToRetry":0}]""");

        var list = await List(s.Tech);

        list.GetArrayLength().ShouldBe(2);
        var online = list.EnumerateArray().Single(x => x.GetProperty("id").GetString() == agent.Id.ToString());
        online.GetProperty("online").GetBoolean().ShouldBeTrue();
        online.GetProperty("lastSeenAt").GetString().ShouldEndWith("Z");
        var machines = online.GetProperty("instruments").EnumerateArray().ToList();
        machines.Select(m => m.GetProperty("name").GetString()).ShouldBe(["Máy A Thử", "Máy B Thử"]);
        machines[0].GetProperty("status").GetProperty("samplesReceived").GetInt32().ShouldBe(3);
        machines[1].GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
        var quiet = list.EnumerateArray().Single(x => x.GetProperty("id").GetString() == idle.Id.ToString());
        quiet.GetProperty("online").GetBoolean().ShouldBeFalse();
        quiet.GetProperty("instruments").GetArrayLength().ShouldBe(0);
        two.ShouldNotBe(elsewhere);
    }

    [Fact]
    public async Task Agent_im_hon_90_giay_thi_offline_va_khong_hien_trang_thai_may()
    {
        await using var s = await StartAsync("pkgstale");
        var agent = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm Thử", "XN-01");
        var machine = await s.Instrument("Máy A Thử", "XN-01");
        await s.Host.TouchAgentAsync(agent, $$"""[{"instrumentId":"{{machine}}","state":"Connected","text":"Đã nối","lastResultAt":null,"samplesReceived":1,"pendingToRetry":0}]""");
        await using (var master = new NpgsqlConnection(fixture.MasterConnection))
        {
            await master.ExecuteAsync("UPDATE agent SET last_seen_at = now() - interval '5 minutes' WHERE id = @id", new { id = agent.Id });
        }

        var item = await One(s.Admin, agent.Id);

        item.GetProperty("online").GetBoolean().ShouldBeFalse();
        item.GetProperty("lastSeenAt").GetString().ShouldNotBeNull();
        item.GetProperty("instruments")[0].GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Sua_agent_chi_doi_ten_204_va_giu_nguyen_ma_may_tram()
    {
        await using var s = await StartAsync("pkgpatch");
        var (id, code) = await Create(s.Admin, "Agent Thử");
        var key = await Pair(s, code);
        var workstation = id.ToString("N");
        var machine = await s.Instrument("Máy A Thử", workstation);

        await ShouldFail(await s.Admin.PatchAsJsonAsync($"/api/agents/{id}", Body(""), Ct), HttpStatusCode.BadRequest, "Nhập tên agent");
        (await s.Admin.PatchAsJsonAsync($"/api/agents/{id}", new { name = "Agent Thử", workstationCode = "XN-03" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldFail(await s.Admin.PatchAsJsonAsync("/api/agents/xyz", Body(), Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.PatchAsJsonAsync($"/api/agents/{Guid.NewGuid()}", Body("Agent Thử"), Ct), HttpStatusCode.NotFound, NotFound);
        await using (var master = new NpgsqlConnection(fixture.MasterConnection))
        {
            (await master.ExecuteScalarAsync<string>("SELECT name FROM agent WHERE id = @id", new { id })).ShouldBe("Agent Thử");
        }

        var response = await s.Admin.PatchAsJsonAsync($"/api/agents/{id}", Body(" Đổi tên Thử "), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var item = await One(s.Admin, id);
        item.GetProperty("name").GetString().ShouldBe("Đổi tên Thử");
        item.GetProperty("workstationCode").GetString().ShouldBe(workstation);
        item.GetProperty("instruments").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ShouldBe([machine.ToString()]);
        (await s.Scalar<string>("SELECT workstation_code FROM instrument WHERE id = @machine", new { machine })).ShouldBe(workstation);
        (await Me(s, key)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Khong_con_duong_dan_cap_khoa_tay()
    {
        await using var s = await StartAsync("pkgnokey");
        var (id, _) = await Create(s.Admin);

        (await s.Admin.PostAsync($"/api/agents/{id}/key", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Xoa_agent_409_khi_con_may_phu_trach_roi_204_va_khoa_het_hieu_luc()
    {
        await using var s = await StartAsync("pkgdel");
        var (id, code) = await Create(s.Admin, "Agent Thử");
        var key = await Pair(s, code);
        var machine = await s.Instrument("Máy A Thử", id.ToString("N"));
        await s.Instrument("Máy B Thử", id.ToString("N"));

        await ShouldFail(await s.Admin.DeleteAsync($"/api/agents/{id}", Ct), HttpStatusCode.Conflict,
            "Agent đang phụ trách 2 máy xét nghiệm — chuyển các máy đó sang agent khác trước khi xoá");
        (await List(s.Admin)).GetArrayLength().ShouldBe(1);

        await s.Exec("UPDATE instrument SET workstation_code = NULL");
        var response = await s.Admin.DeleteAsync($"/api/agents/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await List(s.Admin)).GetArrayLength().ShouldBe(0);
        (await Me(s, key)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldFail(await s.Admin.DeleteAsync($"/api/agents/{id}", Ct), HttpStatusCode.NotFound, NotFound);
        await ShouldFail(await s.Admin.DeleteAsync("/api/agents/xyz", Ct), HttpStatusCode.BadRequest, BadUuid);
        machine.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Agent_phong_kham_khac_vo_hinh_va_khong_bi_thay_doi()
    {
        await using var s = await StartAsync("pkgmine");
        var otherClinic = await s.Host.AddClinicAsync("pkgother", "Phòng khám khác Thử");
        var foreign = await s.Host.AddAgentAsync(otherClinic, "Agent ngoài Thử", "XN-77");
        var mine = await s.Host.AddAgentAsync(s.Clinic, "Agent mình Thử", "XN-01");
        async Task<(string Name, string Workstation, string Hash)> Snapshot()
        {
            await using var master = new NpgsqlConnection(fixture.MasterConnection);
            return await master.QuerySingleAsync<(string, string, string)>(
                "SELECT name, workstation_code, key_hash FROM agent WHERE id = @id", new { id = foreign.Id });
        }

        var before = await Snapshot();

        var list = await List(s.Admin);
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("id").GetString().ShouldBe(mine.Id.ToString());
        await ShouldFail(await s.Admin.PatchAsJsonAsync($"/api/agents/{foreign.Id}", Body("Bị đổi Thử"), Ct), HttpStatusCode.NotFound, NotFound);
        await ShouldFail(await s.Admin.PostAsync($"/api/agents/{foreign.Id}/pairing", null, Ct), HttpStatusCode.NotFound, NotFound);
        await ShouldFail(await s.Admin.DeleteAsync($"/api/agents/{foreign.Id}", Ct), HttpStatusCode.NotFound, NotFound);

        (await Snapshot()).ShouldBe(before);
        // Tên trùng với agent của phòng khám khác không làm khó phòng khám này.
        await Create(s.Admin, "Agent ngoài Thử");
    }

    [Fact]
    public async Task Log_phan_trang_bang_beforeId_khong_lap_khong_sot_moi_nhat_truoc()
    {
        await using var s = await StartAsync("pkglogpage");
        var agent = Guid.NewGuid();
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            expected.Add((await s.Log(agent, null, "agent", note: $"dòng {i}")).ToString());
        }

        expected.Reverse();
        var seen = new List<string>();
        string? before = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = Ids(await Logs(s.Tech, "?limit=3" + (before is null ? "" : $"&beforeId={before}")));
            page.Length.ShouldBeLessThanOrEqualTo(3);
            if (page.Length == 0) break;
            seen.AddRange(page);
            before = page[^1];
        }

        seen.ShouldBe(expected);
        // afterId: chỉ lấy dòng mới hơn dòng đã có.
        Ids(await Logs(s.Tech, $"?afterId={expected[3]}")).ShouldBe(expected.Take(3));
    }

    [Fact]
    public async Task Log_loc_theo_agent_may_chieu_va_tu_khoa_va_tra_du_cac_truong()
    {
        await using var s = await StartAsync("pkglogfilter");
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var machine = await s.Instrument("Máy A Thử", "XN-01");
        var at = new DateTimeOffset(2026, 11, 16, 8, 30, 15, 123, TimeSpan.Zero);
        var inbound = await s.Log(agentA, machine, "in", hex: Convert.ToHexString("BC001234"u8), note: null, at: at);
        var outbound = await s.Log(agentA, machine, "out", hex: "0102");
        var eventLine = await s.Log(agentA, machine, "event", note: "Đã nối 100% cổng_1");
        var fromB = await s.Log(agentB, null, "agent", note: "cảnh báo của B");

        var all = await Logs(s.Admin);
        all.GetArrayLength().ShouldBe(4);
        var first = all.EnumerateArray().Single(x => x.GetProperty("id").GetString() == inbound.ToString());
        first.GetProperty("at").GetString().ShouldBe("2026-11-16T08:30:15.123Z");
        first.GetProperty("agentId").GetString().ShouldBe(agentA.ToString());
        first.GetProperty("instrumentId").GetString().ShouldBe(machine.ToString());
        first.GetProperty("instrumentName").GetString().ShouldBe("Máy A Thử");
        first.GetProperty("direction").GetString().ShouldBe("in");
        first.GetProperty("session").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("hex").GetString().ShouldBe("4243303031323334");
        first.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
        var agentLine = all.EnumerateArray().Single(x => x.GetProperty("id").GetString() == fromB.ToString());
        agentLine.GetProperty("instrumentId").ValueKind.ShouldBe(JsonValueKind.Null);
        agentLine.GetProperty("instrumentName").ValueKind.ShouldBe(JsonValueKind.Null);

        Ids(await Logs(s.Admin, $"?agentId={agentB}")).ShouldBe([fromB.ToString()]);
        Ids(await Logs(s.Admin, $"?agentId={agentA.ToString().ToUpperInvariant()}&instrumentId={machine}")).ShouldBe(
            [eventLine.ToString(), outbound.ToString(), inbound.ToString()]);
        Ids(await Logs(s.Admin, "?direction=in,out")).ShouldBe([outbound.ToString(), inbound.ToString()]);
        Ids(await Logs(s.Admin, "?direction=in,bậy,agent")).ShouldBe([fromB.ToString(), inbound.ToString()]);
        // Từ khoá: trong ghi chú (không phân biệt hoa thường, ký tự đại diện LIKE là nghĩa đen) hoặc trong byte (barcode).
        Ids(await Logs(s.Admin, "?q=%20ĐÃ%20NỐI%20")).ShouldBe([eventLine.ToString()]);
        Ids(await Logs(s.Admin, "?q=100%25")).ShouldBe([eventLine.ToString()]);
        Ids(await Logs(s.Admin, "?q=%25")).ShouldBe([eventLine.ToString()]);
        Ids(await Logs(s.Admin, "?q=cổng_1")).ShouldBe([eventLine.ToString()]);
        Ids(await Logs(s.Admin, "?q=cổngX1")).ShouldBeEmpty();
        Ids(await Logs(s.Admin, "?q=BC0012")).ShouldBe([inbound.ToString()]);
        Ids(await Logs(s.Admin, "?q=%20%20")).Length.ShouldBe(4);
        Ids(await Logs(s.Admin, "?agentId=&direction=&q=")).Length.ShouldBe(4);
    }

    [Fact]
    public async Task Log_limit_mac_dinh_300_toi_da_1000_va_gia_tri_la_bi_gioi_han()
    {
        await using var s = await StartAsync("pkglimit");
        await s.Exec(
            "INSERT INTO agent_log (agent_id, at, direction) SELECT @agent, now(), 'agent' FROM generate_series(1, 1005)", new { agent = Guid.NewGuid() });

        (await Logs(s.Admin)).GetArrayLength().ShouldBe(300);
        (await Logs(s.Admin, "?limit=abc")).GetArrayLength().ShouldBe(300);
        (await Logs(s.Admin, "?limit=0")).GetArrayLength().ShouldBe(300);
        (await Logs(s.Admin, "?limit=5000")).GetArrayLength().ShouldBe(1000);
        (await Logs(s.Admin, "?limit=1000")).GetArrayLength().ShouldBe(1000);
        (await Logs(s.Admin, "?limit=-5")).GetArrayLength().ShouldBe(1);
        (await Logs(s.Admin, "?limit=7")).GetArrayLength().ShouldBe(7);
    }

    [Fact]
    public async Task Log_tham_so_sai_thi_400_dung_cau_bao_loi()
    {
        await using var s = await StartAsync("pkglogbad");

        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?agentId=sai", Ct), HttpStatusCode.BadRequest, "agentId không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?instrumentId=sai", Ct), HttpStatusCode.BadRequest, "instrumentId không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?direction=bậy,x", Ct), HttpStatusCode.BadRequest, "Chiều log không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?beforeId=12a", Ct), HttpStatusCode.BadRequest, "beforeId không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?beforeId=-1", Ct), HttpStatusCode.BadRequest, "beforeId không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?afterId=1.5", Ct), HttpStatusCode.BadRequest, "afterId không hợp lệ");
        await ShouldFail(await s.Admin.GetAsync("/api/agent-logs?beforeId=99999999999999999999999", Ct), HttpStatusCode.BadRequest, "beforeId không hợp lệ");
    }
}
