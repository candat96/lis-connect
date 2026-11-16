using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lis.Server.Api;
using Lis.Server.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

public sealed class SetupEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Key = "khoa-thiet-lap-thu";
    private const string Password = "mat-khau-thu-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string code, string? key = Key, string? name = "Phòng khám Mẫu", string username = "admin",
        string fullName = "Quản trị Thử", string password = Password) =>
        new { setupKey = key, code, name, adminUsername = username, adminFullName = fullName, adminPassword = password };

    private static Task<HttpResponseMessage> Create(HttpClient http, object body) =>
        http.PostAsJsonAsync("/api/setup/clinic", body, Ct);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<bool> DatabaseExistsAsync(string dbName)
    {
        await using var connection = new NpgsqlConnection(fixture.MasterConnection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", dbName);
        return await command.ExecuteScalarAsync(Ct) is not null;
    }

    [Fact]
    public async Task Tao_phong_kham_roi_dang_nhap_bang_tai_khoan_vua_tao()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);

        var response = await Create(host.Anonymous, Body("pk-thu-tao"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await JsonOf(response)).GetProperty("code").GetString().ShouldBe("pk-thu-tao");
        (await DatabaseExistsAsync("lis_pk_thu_tao")).ShouldBeTrue();

        var login = await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pk-thu-tao", username = "admin", password = Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonOf(login);
        body.GetProperty("clinic").GetProperty("name").GetString().ShouldBe("Phòng khám Mẫu");
        body.GetProperty("user").GetProperty("fullName").GetString().ShouldBe("Quản trị Thử");
        body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["quan-tri"]);
    }

    [Fact]
    public async Task Sai_ma_thiet_lap_403_va_khong_tao_csdl()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        host.SetupGuard.Delay = (_, _) => Task.CompletedTask;

        var wrong = await Create(host.Anonymous, Body("pk-thu-sai", key: "khong-dung"));
        var missing = await Create(host.Anonymous, Body("pk-thu-sai", key: null));

        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await JsonOf(wrong)).GetProperty("message").GetString().ShouldBe("Mã thiết lập không đúng");
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DatabaseExistsAsync("lis_pk_thu_sai")).ShouldBeFalse();
        (await host.Anonymous.GetFromJsonAsync<JsonElement>("/api/setup/status", Ct)).GetProperty("hasClinic").GetBoolean()
            .ShouldBe((await new MasterStore(fixture.MasterConnection).ClinicsAsync()).Count > 0);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Pk-Thu")]
    [InlineData("pk thu")]
    [InlineData("pk_thu")]
    [InlineData("phòng-khám")]
    [InlineData("pk-thu-qua-dai-qua-dai-qua-dai-qua-dai")]
    [InlineData("")]
    public async Task Ma_phong_kham_sai_dang_400(string code)
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);

        var response = await Create(host.Anonymous, Body(code));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(response)).GetProperty("message").GetString().ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("mat", "admin", "Mật khẩu tối thiểu 8 ký tự")]
    [InlineData(Password, "ten sai", "Tên đăng nhập chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới")]
    public async Task Tai_khoan_quan_tri_sai_400_va_khong_tao_csdl(string password, string username, string message)
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);

        var response = await Create(host.Anonymous, Body("pk-thu-400", username: username, password: password));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(response)).GetProperty("message").GetString().ShouldBe(message);
        (await DatabaseExistsAsync("lis_pk_thu_400")).ShouldBeFalse();
    }

    [Fact]
    public async Task Truong_la_trong_than_request_400()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);

        var response = await host.Anonymous.PostAsJsonAsync(
            "/api/setup/clinic",
            new { setupKey = Key, code = "pk-thu-la", name = "Mẫu", adminUsername = "admin", adminFullName = "Thử", adminPassword = Password, thua = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Trung_ma_409()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        (await Create(host.Anonymous, Body("pk-thu-trung"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var again = await Create(host.Anonymous, Body("pk-thu-trung", name: "Phòng khám Mẫu khác"));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Hai_request_cung_ma_dong_thoi_dung_mot_201()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Create(host.Anonymous, Body("pk-thu-song")), Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(5);
        (await new MasterStore(fixture.MasterConnection).ClinicsAsync()).Count(c => c.Code == "pk-thu-song").ShouldBe(1);
    }

    [Fact]
    public async Task Tao_that_bai_giua_chung_khong_de_lai_phong_kham_va_thu_lai_duoc()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        var master = new MasterStore(fixture.MasterConnection);

        // Mật khẩu ngắn qua được tầng web nhưng UserStore từ chối — sau khi CSDL đã tạo và migrate xong.
        var failure = await Should.ThrowAsync<ApiException>(
            () => host.Provisioner.CreateAsync("pk-thu-loi", "Phòng khám Mẫu", "admin", "Quản trị Thử", "123"));

        failure.Status.ShouldBe(500);
        failure.Message.ShouldContain("thử lại");
        (await master.FindClinicAsync("pk-thu-loi")).ShouldBeNull();
        (await DatabaseExistsAsync("lis_pk_thu_loi")).ShouldBeFalse();

        var clinic = await host.Provisioner.CreateAsync("pk-thu-loi", "Phòng khám Mẫu", "admin", "Quản trị Thử", Password);
        clinic.Code.ShouldBe("pk-thu-loi");
        (await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pk-thu-loi", username = "admin", password = Password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sai_ma_lien_tiep_cham_dan_moi_lan_them_1_giay_toi_da_10_giay_va_dat_lai_khi_dung()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        var delays = new ConcurrentQueue<TimeSpan>();
        host.SetupGuard.Delay = (span, _) =>
        {
            delays.Enqueue(span);
            return Task.CompletedTask;
        };

        for (var i = 0; i < 13; i++)
        {
            (await Create(host.Anonymous, Body("pk-thu-cham", key: "sai"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        delays.Select(d => (int)d.TotalSeconds).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 10]);

        delays.Clear();
        (await Create(host.Anonymous, Body("pk-thu-cham"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        delays.Select(d => (int)d.TotalSeconds).ShouldBe([10]);

        delays.Clear();
        (await Create(host.Anonymous, Body("pk-thu-cham-2", key: "sai"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Khong_cau_hinh_ma_thi_sinh_24_ky_tu_ngau_nhien_va_dung_duoc()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var guard = host.SetupGuard;

        guard.Generated.ShouldBeTrue();
        guard.Key.Length.ShouldBe(24);

        (await Create(host.Anonymous, Body("pk-thu-sinh", key: guard.Key))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ma_cau_hinh_rong_hoac_chi_khoang_trang_coi_nhu_chua_cau_hinh(string configured)
    {
        await using var host = await ApiHost.StartAsync(fixture, configured);
        host.SetupGuard.Delay = (_, _) => Task.CompletedTask;

        host.SetupGuard.Generated.ShouldBeTrue();
        (await Create(host.Anonymous, Body("pk-thu-trong", key: configured))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Create(host.Anonymous, Body($"pk-thu-trong{configured.Length}", key: host.SetupGuard.Key))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public void Ma_sinh_ra_duoc_ghi_log_muc_Warning_mot_lan_con_ma_cau_hinh_thi_khong_ghi()
    {
        var generated = new SetupGuard(null);
        var logger = new ListLogger();

        generated.LogIfGenerated(logger);

        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Level.ShouldBe(LogLevel.Warning);
        logger.Entries[0].Message.ShouldContain(generated.Key);
        logger.Entries[0].Message.ShouldContain("Mã thiết lập");

        var configured = new ListLogger();
        new SetupGuard("khoa-cau-hinh").LogIfGenerated(configured);
        configured.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Phong_khai_nang_cap_loi_503_rieng_no_phong_khac_van_200()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        var good = await host.AddClinicAsync("pkthutot", "Phòng khám Thử tốt");
        await host.AddUserAsync(good, "admin", Password, "quan-tri");
        var broken = await host.RegisterUnmigratableClinicAsync("pkthuhong", "Phòng khám Thử hỏng");
        var agentKey = await host.AddAgentKeyAsync(broken, "Agent Thử", "XN-01");
        var goodSession = await host.LoginAsync("pkthutot", "admin", Password);

        await host.Provisioner.UpgradeAllAsync();

        host.Provisioner.FailedUpgrades.Keys.ShouldBe([broken.Id]);
        const string message = "Cơ sở dữ liệu của phòng khám chưa nâng cấp được — báo quản trị hệ thống";

        var login = await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pkthuhong", username = "admin", password = Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await JsonOf(login)).GetProperty("message").GetString().ShouldBe(message);

        using var brokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        brokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.IssueToken(broken));
        var me = await host.Anonymous.SendAsync(brokenRequest, Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await JsonOf(me)).GetProperty("message").GetString().ShouldBe(message);

        (await goodSession.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pkthutot", username = "admin", password = Password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var agent = new HttpRequestMessage(HttpMethod.Get, "/agent/v1/me");
        agent.Headers.Add("X-Agent-Key", agentKey);
        (await host.Anonymous.SendAsync(agent, Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>Trạng thái "đã có phòng khám chưa" cần master trống ban đầu nên đứng riêng một lớp (một container).</summary>
public sealed class SetupStatusTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Status_doi_tu_false_sang_true_sau_khi_tao_phong_kham()
    {
        await using var host = await ApiHost.StartAsync(fixture, "khoa-thiet-lap-thu");
        var ct = TestContext.Current.CancellationToken;

        var before = await host.Anonymous.GetFromJsonAsync<JsonElement>("/api/setup/status", ct);
        before.GetProperty("hasClinic").GetBoolean().ShouldBeFalse();

        (await host.Anonymous.PostAsJsonAsync("/api/setup/clinic", new
        {
            setupKey = "khoa-thiet-lap-thu", code = "pk-thu-dau", name = "Phòng khám Mẫu",
            adminUsername = "admin", adminFullName = "Quản trị Thử", adminPassword = "mat-khau-thu-1",
        }, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var after = await host.Anonymous.GetFromJsonAsync<JsonElement>("/api/setup/status", ct);
        after.GetProperty("hasClinic").GetBoolean().ShouldBeTrue();
    }
}
