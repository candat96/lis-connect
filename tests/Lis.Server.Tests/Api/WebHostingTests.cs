using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lis.Server.Api;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>Phục vụ giao diện web (tệp tĩnh + fallback index.html), lỗi JSON dưới /api và header của proxy.</summary>
public sealed class WebHostingTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Index = "<!doctype html><title>Trang thử</title>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string MakeWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lis-web-thu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), Index);
        File.WriteAllText(Path.Combine(root, "assets", "app-abc123.js"), "console.log('thu');");
        return root;
    }

    private Task<ApiHost> StartWithWebAsync(string root) =>
        ApiHost.StartAsync(fixture, settings: new Dictionary<string, string> { ["Web:Root"] = root });

    private static async Task ShouldBeJsonError(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Goc_va_duong_dan_giao_dien_tra_index_html_khong_cache()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);
            foreach (var path in new[] { "/", "/ket-qua/abc", "/index.html" })
            {
                var response = await host.Anonymous.GetAsync(path, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
                (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(Index, path);
                response.Headers.CacheControl!.NoCache.ShouldBeTrue(path);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tai_san_trong_assets_duoc_cache_dai()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);
            var response = await host.Anonymous.GetAsync("/assets/app-abc123.js", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromDays(365));
            response.Headers.CacheControl.Public.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fallback_chi_ap_cho_GET_va_khong_dung_den_api_agent_healthz()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);

            var head = await host.Anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/ket-qua/abc"), Ct);
            head.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await host.Anonymous.PostAsync("/ket-qua/abc", new StringContent("{}", Encoding.UTF8, "application/json"), Ct))
                .StatusCode.ShouldNotBe(HttpStatusCode.OK);

            var agent = await host.Anonymous.GetAsync("/agent/v1/khong-co", Ct);
            (await agent.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Trang thử");

            var health = await host.Anonymous.GetAsync("/healthz", Ct);
            (await health.Content.ReadAsStringAsync(Ct)).ShouldContain("ok");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Trang_agent_cua_web_nhan_index_html_con_api_agent_v1_thi_khong()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);

            foreach (var path in new[] { "/agent", "/log-agent" })
            {
                var response = await host.Anonymous.GetAsync(path, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
                (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(Index, path);
            }

            var api = await host.Anonymous.GetAsync("/agent/v1/khong-co", Ct);
            api.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await api.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Trang thử");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Danh sách route cấp cao của web (web/src/App.tsx) — thêm route mới ở web thì thêm vào đây để bắt va chạm với server.</summary>
    [Fact]
    public async Task Moi_route_cap_cao_cua_web_nhan_index_html()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);
            foreach (var path in new[] { "/dang-nhap", "/thiet-lap", "/ket-qua", "/danh-muc", "/may", "/agent", "/log-agent", "/nhan-su" })
            {
                var response = await host.Anonymous.GetAsync(path, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
                (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(Index, path);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tep_khong_con_trong_assets_tra_404_khong_tra_html()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);
            var response = await host.Anonymous.GetAsync("/assets/app-cu-da-xoa.js", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Trang thử");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Khong_cau_hinh_Web_Root_thi_server_van_chay_va_khong_phuc_vu_web()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        (await host.Anonymous.GetAsync("/healthz", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Anonymous.GetAsync("/", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Duong_dan_api_khong_co_tra_404_json_khong_tra_index_html()
    {
        var root = MakeWebRoot();
        try
        {
            await using var host = await StartWithWebAsync(root);
            var response = await host.Anonymous.GetAsync("/api/khong-co", Ct);
            await ShouldBeJsonError(response, HttpStatusCode.NotFound);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sai_phuong_thuc_duoi_api_tra_405_json()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        await ShouldBeJsonError(await host.Anonymous.GetAsync("/api/auth/login", Ct), HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Than_khong_phai_json_tra_415_json()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        var response = await host.Anonymous.PostAsync("/api/auth/login", new StringContent("x", Encoding.UTF8, "text/plain"), Ct);

        await ShouldBeJsonError(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Mac_dinh_khong_tin_X_Forwarded_For()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var body = await (await host.Anonymous.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);

        body.ShouldNotContain("203.0.113.7");
    }

    [Fact]
    public async Task Bat_Proxy_TrustForwardedHeaders_thi_dung_dia_chi_va_giao_thuc_tu_proxy()
    {
        await using var host = await ApiHost.StartAsync(fixture, settings: new Dictionary<string, string> { ["Proxy:TrustForwardedHeaders"] = "true" });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/ip");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var body = await (await host.Anonymous.SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        body.GetProperty("ip").GetString().ShouldBe("203.0.113.7");
        body.GetProperty("scheme").GetString().ShouldBe("https");
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:5080")]
    [InlineData("", "http://127.0.0.1:5080")]
    [InlineData("http://0.0.0.0:5080", "http://127.0.0.1:5080")]
    [InlineData("http://+:8081", "http://127.0.0.1:8081")]
    [InlineData("http://*:9000;https://*:9443", "http://127.0.0.1:9000")]
    public void Healthcheck_goi_vao_cong_dau_tien_cua_server_qua_127_0_0_1(string? urls, string expected) =>
        HealthCheck.LocalUrl(urls).ShouldBe(expected);

    [Fact]
    public async Task Healthcheck_thoat_0_khi_healthz_tra_200_va_1_khi_server_khong_len()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        (await HealthCheck.RunAsync(host.Anonymous)).ShouldBe(0);

        using var dead = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        (await HealthCheck.RunAsync(dead)).ShouldBe(1);
    }

    [Fact]
    public async Task Tep_chay_voi_healthcheck_thoat_1_khi_khong_co_server_lang_nghe()
    {
        var dll = typeof(Program).Assembly.Location;
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{dll}\" --healthcheck") { RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:1";
        using var process = System.Diagnostics.Process.Start(start)!;

        await process.WaitForExitAsync(Ct);

        process.ExitCode.ShouldBe(1);
    }
}
