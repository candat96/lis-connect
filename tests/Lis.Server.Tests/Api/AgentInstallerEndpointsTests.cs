using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>Phát tệp cài agent (<c>/api/agents/installer</c>): chỉ Quản trị tải, tệp và SHA-256 lấy từ <c>Agent:DistDir</c>.</summary>
public sealed class AgentInstallerEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>, IDisposable
{
    private const string Password = "mat-khau-thu-1";
    private const string AdminOnly = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";
    private const string Missing = "Máy chủ chưa có tệp cài agent — xem mục AGENT_DIST_DIR trong hướng dẫn cài đặt";
    private static readonly byte[] FirstBuild = Encoding.UTF8.GetBytes("MZ-thu");

    private readonly List<string> _dirs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private string NewDistDir()
    {
        var dir = Directory.CreateTempSubdirectory("lis-agent-dist-").FullName;
        _dirs.Add(dir);
        return dir;
    }

    private static void WriteInstaller(string dir, byte[] content) => File.WriteAllBytes(Path.Combine(dir, "LisAgent.exe"), content);

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<ApiHost> StartAsync(ServerFixture fixture, string code, string? distDir, string tech = "ktv")
    {
        var settings = distDir is null ? null : new Dictionary<string, string> { ["Agent:DistDir"] = distDir };
        var host = await ApiHost.StartAsync(fixture, settings: settings);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        return host;
    }

    private static async Task<JsonElement> InfoOf(HttpClient client)
    {
        var response = await client.GetAsync("/api/agents/installer/info", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task Info_tra_phien_ban_sha256_va_kich_thuoc()
    {
        var dir = NewDistDir();
        WriteInstaller(dir, FirstBuild);
        await File.WriteAllTextAsync(Path.Combine(dir, "version.txt"), "0.2.0\n", Ct);
        await using var host = await StartAsync(fixture, "dinfo", dir);
        var admin = await host.LoginAsync("dinfo", "admin", Password);

        var info = await InfoOf(admin);

        info.GetProperty("available").GetBoolean().ShouldBeTrue();
        info.GetProperty("version").GetString().ShouldBe("0.2.0");
        info.GetProperty("sha256").GetString().ShouldBe(Sha256(FirstBuild));
        info.GetProperty("size").GetInt64().ShouldBe(FirstBuild.Length);
    }

    [Fact]
    public async Task Tai_tep_dung_noi_dung_va_header()
    {
        var dir = NewDistDir();
        WriteInstaller(dir, FirstBuild);
        await File.WriteAllTextAsync(Path.Combine(dir, "version.txt"), "0.2.0\n", Ct);
        await using var host = await StartAsync(fixture, "dtai", dir);
        var admin = await host.LoginAsync("dtai", "admin", Password);

        var response = await admin.GetAsync("/api/agents/installer", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(FirstBuild);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.microsoft.portable-executable");
        response.Content.Headers.GetValues("Content-Disposition").Single().ShouldBe("attachment; filename=\"LisAgent.exe\"");
        response.Headers.GetValues("X-Agent-Version").Single().ShouldBe("0.2.0");
        response.Headers.GetValues("X-Agent-Sha256").Single().ShouldBe(Sha256(FirstBuild));
    }

    [Fact]
    public async Task Chua_cau_hinh_thi_info_available_false_va_tai_404_json()
    {
        await using var host = await StartAsync(fixture, "dnone", distDir: null);
        var admin = await host.LoginAsync("dnone", "admin", Password);

        var info = await InfoOf(admin);
        info.GetProperty("available").GetBoolean().ShouldBeFalse();
        info.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Null);
        info.GetProperty("sha256").ValueKind.ShouldBe(JsonValueKind.Null);
        info.GetProperty("size").ValueKind.ShouldBe(JsonValueKind.Null);

        var response = await admin.GetAsync("/api/agents/installer", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("statusCode").GetInt32().ShouldBe(404);
        body.GetProperty("message").GetString().ShouldBe(Missing);
    }

    [Fact]
    public async Task Ky_thuat_vien_403_chua_dang_nhap_401()
    {
        var dir = NewDistDir();
        WriteInstaller(dir, FirstBuild);
        await using var host = await StartAsync(fixture, "dperm", dir);
        var tech = await host.LoginAsync("dperm", "ktv", Password);

        var infoForTech = await tech.GetAsync("/api/agents/installer/info", Ct);
        infoForTech.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await infoForTech.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("message").GetString().ShouldBe(AdminOnly);

        var fileForTech = await tech.GetAsync("/api/agents/installer", Ct);
        fileForTech.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fileForTech.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("message").GetString().ShouldBe(AdminOnly);

        (await host.Anonymous.GetAsync("/api/agents/installer/info", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.Anonymous.GetAsync("/api/agents/installer", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Doi_tep_thi_sha256_doi_theo()
    {
        var dir = NewDistDir();
        var path = Path.Combine(dir, "LisAgent.exe");
        WriteInstaller(dir, FirstBuild);
        await using var host = await StartAsync(fixture, "dchg", dir);
        var admin = await host.LoginAsync("dchg", "admin", Password);
        (await InfoOf(admin)).GetProperty("sha256").GetString().ShouldBe(Sha256(FirstBuild));

        // Khác độ dài: kích thước và SHA-256 đều phải đổi.
        var second = Encoding.UTF8.GetBytes("MZ-thu-ban-moi-dai-hon");
        WriteInstaller(dir, second);
        var changed = await InfoOf(admin);
        changed.GetProperty("sha256").GetString().ShouldBe(Sha256(second));
        changed.GetProperty("size").GetInt64().ShouldBe(second.Length);

        // Cùng độ dài, khác nội dung, thời điểm sửa khác: vẫn phải tính lại (cache dựa trên độ dài + thời điểm sửa).
        var sameLength = Encoding.UTF8.GetBytes("MZ-thu-ban-moi-dai-hom");
        sameLength.Length.ShouldBe(second.Length);
        WriteInstaller(dir, sameLength);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        var sameSizeInfo = await InfoOf(admin);
        sameSizeInfo.GetProperty("sha256").GetString().ShouldBe(Sha256(sameLength));
        sameSizeInfo.GetProperty("size").GetInt64().ShouldBe(sameLength.Length);
    }
}
