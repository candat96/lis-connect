using Shouldly;
using Xunit;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lis.Server.Api;

namespace Lis.Server.Tests.Api;

public sealed class AuthEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string Invalid = "Sai mã phòng khám, tên đăng nhập hoặc mật khẩu";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> Login(HttpClient http, string clinic, string user, string password) =>
        http.PostAsJsonAsync("/api/auth/login", new { clinicCode = clinic, username = user, password }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Login_dung_tra_token_va_thong_tin()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkthu", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");

        var response = await Login(host.Anonymous, "pkthu", "admin", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonOf(response);
        body.GetProperty("token").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("clinic").GetProperty("code").GetString().ShouldBe("pkthu");
        body.GetProperty("clinic").GetProperty("name").GetString().ShouldBe("Phòng khám Thử");
        var user = body.GetProperty("user");
        user.GetProperty("username").GetString().ShouldBe("admin");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["quan-tri"]);
        user.GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Login_ma_phong_kham_khong_phan_biet_hoa_thuong_va_khoang_trang()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkhoa", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");

        (await Login(host.Anonymous, "  PKHoa ", " admin ", Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_sai_mat_khau_401_cung_thong_bao_voi_sai_phong_kham()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pksai", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");

        var wrongPassword = await Login(host.Anonymous, "pksai", "admin", "sai-mat-khau");
        var wrongClinic = await Login(host.Anonymous, "khong-co", "admin", Password);
        var wrongUser = await Login(host.Anonymous, "pksai", "khong-co", Password);

        foreach (var response in new[] { wrongPassword, wrongClinic, wrongUser })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await JsonOf(response);
            body.GetProperty("statusCode").GetInt32().ShouldBe(401);
            body.GetProperty("message").GetString().ShouldBe(Invalid);
        }
    }

    [Fact]
    public async Task Login_sai_5_lan_thi_429()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkkhoa", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");

        for (var i = 0; i < 4; i++)
        {
            (await Login(host.Anonymous, "pkkhoa", "admin", "sai")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Lis.Data khoá ngay ở lần sai thứ 5; sau đó mật khẩu đúng cũng bị từ chối.
        var fifth = await Login(host.Anonymous, "pkkhoa", "admin", "sai");
        fifth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var locked = await Login(host.Anonymous, "pkkhoa", "admin", Password);
        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await JsonOf(locked)).GetProperty("message").GetString()
            .ShouldBe("Tài khoản tạm khoá do nhập sai nhiều lần — thử lại sau 15 phút");
    }

    [Fact]
    public async Task Login_tai_khoan_ngung_hoat_dong_401()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkngung", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        await host.SetActiveAsync(clinic, "ktv", false);

        var response = await Login(host.Anonymous, "pkngung", "ktv", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(response)).GetProperty("message").GetString().ShouldBe("Tài khoản đã ngừng hoạt động");
    }

    [Fact]
    public async Task Login_thieu_truong_400_va_co_truong_la_400()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkla", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");

        var extra = await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pkla", username = "admin", password = Password, role = "quan-tri" },
            TestContext.Current.CancellationToken);
        extra.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(extra)).GetProperty("statusCode").GetInt32().ShouldBe(400);

        var missing = await host.Anonymous.PostAsJsonAsync(
            "/api/auth/login", new { clinicCode = "pkla", username = "admin" }, TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(missing)).GetProperty("message").GetString().ShouldBe("Chưa nhập mật khẩu");

        var broken = await host.Anonymous.PostAsync(
            "/api/auth/login", new StringContent("{khong-phai-json", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_khong_token_401()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        var response = await host.Anonymous.GetAsync("/api/auth/me", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe(401);
        body.GetProperty("message").GetString().ShouldBe("Phiên đăng nhập hết hạn — đăng nhập lại");
    }

    [Fact]
    public async Task Chua_dang_nhap_thi_401_truoc_khi_kiem_than_request()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        const string Expired = "Phiên đăng nhập hết hạn — đăng nhập lại";

        var bodies = new[] { """{"name":"Mẫu","la":1}""", "{khong-phai-json" };
        foreach (var json in bodies)
        {
            var response = await host.Anonymous.PostAsync(
                ApiHost.EchoRoute, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await JsonOf(response)).GetProperty("message").GetString().ShouldBe(Expired);
        }
    }

    [Fact]
    public async Task Da_dang_nhap_thi_route_co_phien_van_cam_truong_la()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkecho", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var client = await host.LoginAsync("pkecho", "ktv", Password);

        (await client.PostAsJsonAsync(ApiHost.EchoRoute, new { name = "Mẫu" }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(ApiHost.EchoRoute, new { name = "Mẫu", la = 1 }, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_token_hop_le_tra_user_va_clinic()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkme", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var client = await host.LoginAsync("pkme", "ktv", Password);

        var response = await client.GetAsync("/api/auth/me", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await JsonOf(response);
        body.GetProperty("clinic").GetProperty("code").GetString().ShouldBe("pkme");
        body.GetProperty("user").GetProperty("username").GetString().ShouldBe("ktv");
        body.TryGetProperty("token", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Tai_khoan_bi_ngung_sau_khi_dang_nhap_thi_401()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkngung2", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var client = await host.LoginAsync("pkngung2", "ktv", Password);
        (await client.GetAsync("/api/auth/me", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await host.SetActiveAsync(clinic, "ktv", false);

        var response = await client.GetAsync("/api/auth/me", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(response)).GetProperty("message").GetString().ShouldBe("Tài khoản đã ngừng hoạt động");
    }

    [Fact]
    public async Task Token_ky_bang_khoa_khac_401()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync("pkkhac", "Phòng khám Thử");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var client = await host.LoginAsync("pkkhac", "ktv", Password);
        var real = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var (userId, clinicId) = new SessionTokens(ApiHost.TestSecret, TimeProvider.System).Read(real)!.Value;
        var forged = new SessionTokens("mot-khoa-khac-hoan-toan-0123456789", TimeProvider.System).Issue(userId, clinicId);

        var response = await host.Anonymous.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/me") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", forged) } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(response)).GetProperty("message").GetString().ShouldBe("Phiên đăng nhập hết hạn — đăng nhập lại");
    }

    [Fact]
    public void Token_het_han_sau_12_gio_va_doc_lai_duoc_truoc_do()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
        var tokens = new SessionTokens(ApiHost.TestSecret, clock);
        var (user, clinic) = (Guid.NewGuid(), Guid.NewGuid());
        var token = tokens.Issue(user, clinic);

        clock.Advance(TimeSpan.FromHours(11));
        tokens.Read("Bearer " + token).ShouldBe((user, clinic));
        clock.Advance(TimeSpan.FromHours(1));
        tokens.Read("Bearer " + token).ShouldBeNull();
    }

    [Fact]
    public void ServerOptions_tu_choi_khoa_bi_mat_yeu()
    {
        static ServerOptions Make(string? secret)
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
            configuration["Jwt:Secret"] = secret;
            return ServerOptions.From(configuration);
        }

        Should.Throw<InvalidOperationException>(() => Make(null));
        Should.Throw<InvalidOperationException>(() => Make("CHANGE_ME"));
        Should.Throw<InvalidOperationException>(() => Make("ngan-qua"));
        Make(ApiHost.TestSecret).LocalUtcOffsetMinutes.ShouldBe(420);
    }
}
