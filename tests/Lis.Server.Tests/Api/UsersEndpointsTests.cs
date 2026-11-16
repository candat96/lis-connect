using Shouldly;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lis.Server.Tests.Api;

public sealed class UsersEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string NewPassword = "mat-khau-moi-2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldBe(message);
    }

    private sealed record Setup(ApiHost Host, HttpClient Admin, HttpClient Tech) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private async Task<Setup> StartAsync(string code)
    {
        var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        return new Setup(host, await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password));
    }

    private static async Task<JsonElement> UserOf(HttpClient admin, string username)
    {
        var list = await JsonOf(await admin.GetAsync("/api/users", Ct));
        return list.EnumerateArray().Single(u => u.GetProperty("username").GetString() == username);
    }

    private static async Task<string> IdOf(HttpClient admin, string username) => (await UserOf(admin, username)).GetProperty("id").GetString()!;

    private static object NewUser(string username = "mau1", string fullName = "Nguyễn Mẫu", string[]? roles = null, string password = "mat-khau-mau-1") =>
        new { username, fullName, title = (string?)null, roles = roles ?? ["tiep-nhan"], password };

    [Fact]
    public async Task Danh_sach_tra_dung_truong_va_sap_theo_ten_dang_nhap()
    {
        await using var s = await StartAsync("pkulist");

        var response = await s.Admin.GetAsync("/api/users", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = (await JsonOf(response)).EnumerateArray().ToList();
        list.Select(u => u.GetProperty("username").GetString()).ShouldBe(["admin", "ktv"]);
        var ktv = list[1];
        Guid.Parse(ktv.GetProperty("id").GetString()!).ShouldNotBe(Guid.Empty);
        ktv.GetProperty("fullName").GetString().ShouldBe("ktv Thử");
        ktv.GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);
        ktv.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["ky-thuat-vien"]);
        ktv.GetProperty("active").GetBoolean().ShouldBeTrue();
        ktv.GetProperty("lockedUntil").ValueKind.ShouldBe(JsonValueKind.Null);
        // ktv đã đăng nhập khi dựng test → có lastLoginAt dạng ISO 8601 UTC có mili giây (như toISOString của JS).
        ktv.GetProperty("lastLoginAt").GetString()!.ShouldMatch(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        ktv.EnumerateObject().Select(p => p.Name).Order().ShouldBe(
            ["active", "fullName", "id", "lastLoginAt", "lockedUntil", "roles", "title", "username"]);
    }

    [Fact]
    public async Task Danh_sach_hien_khoa_tam_khi_nhap_sai_nhieu_lan()
    {
        await using var s = await StartAsync("pkulock");
        for (var i = 0; i < 5; i++)
        {
            await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkulock", username = "ktv", password = "sai" }, Ct);
        }

        var ktv = await UserOf(s.Admin, "ktv");

        DateTimeOffset.Parse(ktv.GetProperty("lockedUntil").GetString()!).ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Khong_phai_quan_tri_thi_403_o_moi_route_quan_ly()
    {
        await using var s = await StartAsync("pku403");
        var id = await IdOf(s.Admin, "ktv");
        const string Message = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";

        await ShouldFail(await s.Tech.GetAsync("/api/users", Ct), HttpStatusCode.Forbidden, Message);
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/users", NewUser(), Ct), HttpStatusCode.Forbidden, Message);
        await ShouldFail(
            await s.Tech.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "quan-tri" }, active = true }, Ct),
            HttpStatusCode.Forbidden, Message);
    }

    [Fact]
    public async Task Chua_dang_nhap_thi_401()
    {
        await using var s = await StartAsync("pku401");

        (await s.Host.Anonymous.GetAsync("/api/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.PostAsJsonAsync("/api/users/me/password", new { currentPassword = "a", newPassword = "bbbbbbbb" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tao_tai_khoan_tra_201_id_va_xuat_hien_trong_danh_sach()
    {
        await using var s = await StartAsync("pkucreate");

        var response = await s.Admin.PostAsJsonAsync(
            "/api/users", new { username = "mau.1_x-y", fullName = "  Nguyễn Mẫu ", title = " Bác sĩ ", roles = new[] { "tiep-nhan", "tiep-nhan", "truong-khoa" }, password = "mat-khau-mau-1" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await JsonOf(response);
        created.EnumerateObject().Select(p => p.Name).ShouldBe(["id"]);
        var user = await UserOf(s.Admin, "mau.1_x-y");
        user.GetProperty("id").GetString().ShouldBe(created.GetProperty("id").GetString());
        user.GetProperty("fullName").GetString().ShouldBe("Nguyễn Mẫu");
        user.GetProperty("title").GetString().ShouldBe("Bác sĩ");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["tiep-nhan", "truong-khoa"]);
        user.GetProperty("active").GetBoolean().ShouldBeTrue();
        // Đăng nhập được bằng mật khẩu vừa đặt.
        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkucreate", username = "mau.1_x-y", password = "mat-khau-mau-1" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tao_tai_khoan_title_rong_luu_null()
    {
        await using var s = await StartAsync("pkutitle");

        (await s.Admin.PostAsJsonAsync("/api/users", new { username = "mau2", fullName = "Mẫu Hai", title = "   ", roles = new[] { "tiep-nhan" }, password = "mat-khau-mau-1" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await UserOf(s.Admin, "mau2")).GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Tao_tai_khoan_trung_ten_dang_nhap_409()
    {
        await using var s = await StartAsync("pkudup");

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/users", NewUser("ktv"), Ct), HttpStatusCode.Conflict, "Tên đăng nhập đã tồn tại");
    }

    [Theory]
    [InlineData("", "Nguyễn Mẫu", "tiep-nhan", "mat-khau-mau-1", "Nhập tên đăng nhập")]
    [InlineData("co dau cach", "Nguyễn Mẫu", "tiep-nhan", "mat-khau-mau-1", "Tên đăng nhập chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới")]
    [InlineData("tên-có-dấu", "Nguyễn Mẫu", "tiep-nhan", "mat-khau-mau-1", "Tên đăng nhập chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới")]
    [InlineData("mau3", "", "tiep-nhan", "mat-khau-mau-1", "Nhập họ tên")]
    [InlineData("mau3", "Nguyễn Mẫu", "", "mat-khau-mau-1", "Chọn ít nhất một vai trò")]
    [InlineData("mau3", "Nguyễn Mẫu", "thu-ngan", "mat-khau-mau-1", "Vai trò không hợp lệ")]
    [InlineData("mau3", "Nguyễn Mẫu", "tiep-nhan", "ngan", "Mật khẩu tối thiểu 8 ký tự")]
    public async Task Tao_tai_khoan_than_sai_400_dung_thong_bao(string username, string fullName, string role, string password, string message)
    {
        await using var s = await StartAsync("pkuinv" + Math.Abs(HashCode.Combine(username, fullName, role, password)) % 100000);
        string[] roles = role == "" ? [] : [role];

        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/users", new { username, fullName, title = (string?)null, roles, password }, Ct),
            HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Tao_tai_khoan_truong_la_hoac_thieu_roles_400()
    {
        await using var s = await StartAsync("pkucextra");

        (await s.Admin.PostAsJsonAsync("/api/users", new { username = "mau4", fullName = "Mẫu", roles = new[] { "tiep-nhan" }, password = "mat-khau-mau-1", active = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/users", new { username = "mau4", fullName = "Mẫu", password = "mat-khau-mau-1" }, Ct),
            HttpStatusCode.BadRequest, "Chọn ít nhất một vai trò");
    }

    [Fact]
    public async Task Sua_tai_khoan_204_va_doi_duoc_ho_ten_chuc_danh_vai_tro_trang_thai()
    {
        await using var s = await StartAsync("pkuupd");
        var id = await IdOf(s.Admin, "ktv");

        var response = await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = " Trần Mẫu ", title = "Trưởng khoa", roles = new[] { "truong-khoa", "truong-khoa" }, active = false }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        var user = await UserOf(s.Admin, "ktv");
        user.GetProperty("fullName").GetString().ShouldBe("Trần Mẫu");
        user.GetProperty("title").GetString().ShouldBe("Trưởng khoa");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["truong-khoa"]);
        user.GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Sua_tai_khoan_khong_co_newPassword_giu_mat_khau_cu()
    {
        await using var s = await StartAsync("pkukeep");
        var id = await IdOf(s.Admin, "ktv");

        (await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" }, active = true, newPassword = "" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkukeep", username = "ktv", password = Password }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Dat_lai_mat_khau_doi_mat_khau_va_mo_khoa_tai_khoan()
    {
        await using var s = await StartAsync("pkureset");
        var id = await IdOf(s.Admin, "ktv");
        for (var i = 0; i < 5; i++)
        {
            await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkureset", username = "ktv", password = "sai" }, Ct);
        }

        (await UserOf(s.Admin, "ktv")).GetProperty("lockedUntil").ValueKind.ShouldBe(JsonValueKind.String);

        (await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" }, active = true, newPassword = NewPassword }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await UserOf(s.Admin, "ktv")).GetProperty("lockedUntil").ValueKind.ShouldBe(JsonValueKind.Null);
        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkureset", username = "ktv", password = NewPassword }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkureset", username = "ktv", password = Password }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dat_lai_mat_khau_ngan_400()
    {
        await using var s = await StartAsync("pkushort");
        var id = await IdOf(s.Admin, "ktv");

        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" }, active = true, newPassword = "ngan" }, Ct),
            HttpStatusCode.BadRequest, "Mật khẩu tối thiểu 8 ký tự");
    }

    [Theory]
    [InlineData("", "ky-thuat-vien", "Nhập họ tên")]
    [InlineData("Mẫu", "", "Chọn ít nhất một vai trò")]
    [InlineData("Mẫu", "thu-ngan", "Vai trò không hợp lệ")]
    public async Task Sua_tai_khoan_than_sai_400_dung_thong_bao(string fullName, string role, string message)
    {
        await using var s = await StartAsync("pkuuinv" + Math.Abs(HashCode.Combine(fullName, role)) % 100000);
        var id = await IdOf(s.Admin, "ktv");
        string[] roles = role == "" ? [] : [role];

        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName, roles, active = true }, Ct), HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Sua_tai_khoan_thieu_active_hoac_truong_la_hoac_id_khong_phai_uuid_400()
    {
        await using var s = await StartAsync("pkuu400");
        var id = await IdOf(s.Admin, "ktv");

        (await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" }, active = true, username = "doi-ten" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldFail(
            await s.Admin.PutAsJsonAsync("/api/users/khong-phai-uuid", new { fullName = "Mẫu", roles = new[] { "ky-thuat-vien" }, active = true }, Ct),
            HttpStatusCode.BadRequest, "Validation failed (uuid is expected)");
    }

    [Fact]
    public async Task Sua_tai_khoan_khong_ton_tai_hoac_cua_phong_kham_khac_404()
    {
        await using var s = await StartAsync("pku404a");
        var other = await s.Host.AddClinicAsync("pku404b", "Phòng khám Thử Khác");
        await s.Host.AddUserAsync(other, "admin", Password, "quan-tri");
        await s.Host.AddUserAsync(other, "nv", Password, "tiep-nhan");
        var otherAdmin = await s.Host.LoginAsync("pku404b", "admin", Password);
        var otherId = await IdOf(otherAdmin, "nv");
        var body = new { fullName = "Mẫu", roles = new[] { "tiep-nhan" }, active = true };

        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/users/{otherId}", body, Ct), HttpStatusCode.NotFound, "Không tìm thấy tài khoản");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/users/{Guid.NewGuid()}", body, Ct), HttpStatusCode.NotFound, "Không tìm thấy tài khoản");
        // Tài khoản phòng khám kia không bị đổi.
        (await UserOf(otherAdmin, "nv")).GetProperty("fullName").GetString().ShouldBe("nv Thử");
    }

    private const string LastAdmin =
        "Phải còn ít nhất một tài khoản Quản trị đang hoạt động — không thể khoá hoặc bỏ vai trò Quản trị của tài khoản này";

    [Fact]
    public async Task Khong_duoc_khoa_hoac_bo_vai_tro_cua_quan_tri_cuoi_cung_409()
    {
        await using var s = await StartAsync("pkulast");
        var id = await IdOf(s.Admin, "admin");

        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "quan-tri" }, active = false }, Ct),
            HttpStatusCode.Conflict, LastAdmin);
        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Mẫu", roles = new[] { "tiep-nhan" }, active = true }, Ct),
            HttpStatusCode.Conflict, LastAdmin);
        // Vẫn là quản trị đang hoạt động, chưa đổi gì.
        var user = await UserOf(s.Admin, "admin");
        user.GetProperty("active").GetBoolean().ShouldBeTrue();
        user.GetProperty("fullName").GetString().ShouldBe("admin Thử");
        // Sửa họ tên mà vẫn giữ vai trò và hoạt động thì được.
        (await s.Admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Quản Trị Mẫu", roles = new[] { "quan-tri", "tiep-nhan" }, active = true }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Co_quan_tri_thu_hai_thi_khoa_duoc_quan_tri_con_lai_roi_den_luot_cuoi_409()
    {
        await using var s = await StartAsync("pkutwo");
        (await s.Admin.PostAsJsonAsync("/api/users", NewUser("admin2", roles: ["quan-tri"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var admin1 = await IdOf(s.Admin, "admin");
        var admin2 = await IdOf(s.Admin, "admin2");

        // Khoá admin2 được vì admin vẫn còn.
        (await s.Admin.PutAsJsonAsync($"/api/users/{admin2}", new { fullName = "Mẫu", roles = new[] { "quan-tri" }, active = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // Giờ admin là quản trị hoạt động cuối cùng.
        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/users/{admin1}", new { fullName = "Mẫu", roles = new[] { "quan-tri" }, active = false }, Ct),
            HttpStatusCode.Conflict, LastAdmin);
    }

    [Fact]
    public async Task Hai_quan_tri_cung_luc_tu_khoa_nhau_chi_mot_ben_thanh_cong()
    {
        await using var s = await StartAsync("pkurace");
        (await s.Admin.PostAsJsonAsync("/api/users", NewUser("admin2", roles: ["quan-tri"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var admin1 = await IdOf(s.Admin, "admin");
        var admin2 = await IdOf(s.Admin, "admin2");
        var body = new { fullName = "Mẫu", roles = new[] { "quan-tri" }, active = false };

        var results = await Task.WhenAll(
            s.Admin.PutAsJsonAsync($"/api/users/{admin2}", body, Ct),
            s.Admin.PutAsJsonAsync($"/api/users/{admin1}", body, Ct));

        results.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.NoContent, HttpStatusCode.Conflict]);
    }

    [Fact]
    public async Task Tu_doi_mat_khau_204_moi_vai_tro_va_dang_nhap_duoc_bang_mat_khau_moi()
    {
        await using var s = await StartAsync("pkume");

        var response = await s.Tech.PostAsJsonAsync("/api/users/me/password", new { currentPassword = Password, newPassword = NewPassword }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkume", username = "ktv", password = NewPassword }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkume", username = "ktv", password = Password }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tu_doi_mat_khau_sai_mat_khau_hien_tai_400_va_khong_doi_gi()
    {
        await using var s = await StartAsync("pkumebad");

        await ShouldFail(
            await s.Tech.PostAsJsonAsync("/api/users/me/password", new { currentPassword = "sai-mat-khau", newPassword = NewPassword }, Ct),
            HttpStatusCode.BadRequest, "Mật khẩu hiện tại không đúng");

        (await s.Host.Anonymous.PostAsJsonAsync("/api/auth/login", new { clinicCode = "pkumebad", username = "ktv", password = Password }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tu_doi_mat_khau_than_sai_400()
    {
        await using var s = await StartAsync("pkumeinv");

        await ShouldFail(
            await s.Tech.PostAsJsonAsync("/api/users/me/password", new { currentPassword = Password, newPassword = "ngan" }, Ct),
            HttpStatusCode.BadRequest, "Mật khẩu tối thiểu 8 ký tự");
        await ShouldFail(
            await s.Tech.PostAsJsonAsync("/api/users/me/password", new { currentPassword = "", newPassword = NewPassword }, Ct),
            HttpStatusCode.BadRequest, "Nhập mật khẩu hiện tại");
        (await s.Tech.PostAsJsonAsync("/api/users/me/password", new { currentPassword = Password, newPassword = NewPassword, la = 1 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
