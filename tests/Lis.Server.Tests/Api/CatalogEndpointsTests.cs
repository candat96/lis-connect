using Shouldly;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Lis.Server.Tests.Api;

public sealed class CatalogEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string AdminOnly = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";

    private static int _validationCase;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldBe(message);
    }

    private sealed record Setup(ApiHost Host, string Code, HttpClient Admin, HttpClient Tech, string Connection, Guid Serum) : IAsyncDisposable
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
    }

    private async Task<Setup> StartAsync(string code)
    {
        var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var connection = fixture.TenantTemplate.Replace("{db}", $"lis_{code}", StringComparison.Ordinal);
        await using var db = new NpgsqlConnection(connection);
        var serum = await db.ExecuteScalarAsync<Guid>("SELECT id FROM specimen_type WHERE code = 'SERUM'");
        return new Setup(host, code, await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password), connection, serum);
    }

    private static Dictionary<string, object?> Body(Guid specimen, string code = "GLU", string name = "Glucose Mẫu", Action<Dictionary<string, object?>>? edit = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["code"] = code, ["name"] = name, ["groupName"] = null, ["specimenTypeId"] = specimen, ["unit"] = null, ["valueType"] = "so",
            ["decimals"] = null, ["printOrder"] = null, ["isPanel"] = false, ["autoApprove"] = false, ["active"] = true, ["hisCode"] = null,
            ["childIds"] = Array.Empty<Guid>(), ["ranges"] = Array.Empty<object>(), ["qualitativeRules"] = Array.Empty<object>(),
        };
        edit?.Invoke(body);
        return body;
    }

    private static object Range(string? sex = null, int? from = null, int? to = null, Guid? instrument = null, decimal? low = null, decimal? high = null,
        decimal? criticalLow = null, decimal? criticalHigh = null, string? text = null) =>
        new { sex, ageFromDays = from, ageToDays = to, instrumentId = instrument, low, high, criticalLow, criticalHigh, textRange = text };

    private static async Task<Guid> Create(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync("/api/catalog/services", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return Guid.Parse((await JsonOf(response)).GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> Detail(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/catalog/services/{id}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonOf(response);
    }

    private static async Task<JsonElement> Tree(HttpClient client) => await JsonOf(await client.GetAsync("/api/catalog/services", Ct));

    [Fact]
    public async Task Chua_dang_nhap_thi_401_o_moi_route()
    {
        await using var s = await StartAsync("pkc401");
        var id = Guid.NewGuid();

        (await s.Host.Anonymous.GetAsync("/api/catalog/services", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.GetAsync("/api/catalog/meta", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.GetAsync($"/api/catalog/services/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.PostAsJsonAsync("/api/catalog/services", Body(s.Serum), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.PutAsJsonAsync($"/api/catalog/services/{id}", Body(s.Serum), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await s.Host.Anonymous.DeleteAsync($"/api/catalog/services/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Khong_phai_quan_tri_xem_duoc_nhung_sua_xoa_thi_403()
    {
        await using var s = await StartAsync("pkc403");
        var id = await Create(s.Admin, Body(s.Serum));

        (await s.Tech.GetAsync("/api/catalog/services", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Tech.GetAsync("/api/catalog/meta", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Tech.GetAsync($"/api/catalog/services/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, "KHAC"), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PutAsJsonAsync($"/api/catalog/services/{id}", Body(s.Serum), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.DeleteAsync($"/api/catalog/services/{id}", Ct), HttpStatusCode.Forbidden, AdminOnly);
        // Kiểm đầu vào chạy trước kiểm quyền (pipe trước handler).
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, "", "x"), Ct), HttpStatusCode.BadRequest, "Nhập mã dịch vụ");
    }

    [Fact]
    public async Task Cay_dich_vu_goi_co_chi_so_con_theo_thu_tu_va_khong_lap_o_goc()
    {
        await using var s = await StartAsync("pkctree");
        var urea = await Create(s.Admin, Body(s.Serum, "UREA", "Urea Mẫu", b => { b["groupName"] = "Hóa sinh"; b["unit"] = "mmol/L"; b["decimals"] = 1; }));
        var creat = await Create(s.Admin, Body(s.Serum, "CREA", "Creatinin Mẫu", b => { b["groupName"] = "Hóa sinh"; b["autoApprove"] = true; }));
        await Create(s.Admin, Body(s.Serum, "GLU", "Glucose Mẫu", b => { b["groupName"] = "Hóa sinh"; b["active"] = false; }));
        await Create(s.Admin, Body(s.Serum, "AAA", "Chỉ số Thử", b => b["groupName"] = null));
        await Create(s.Admin, Body(s.Serum, "THN", "Thận Mẫu", b => { b["groupName"] = "Hóa sinh"; b["isPanel"] = true; b["childIds"] = new[] { urea, creat }; }));

        var tree = await Tree(s.Tech);

        // Không nhóm (rỗng) đứng trước; trong nhóm theo mã; chỉ số con không lặp ở gốc.
        tree.EnumerateArray().Select(x => x.GetProperty("code").GetString()).ShouldBe(["AAA", "GLU", "THN"]);
        var panel = tree.EnumerateArray().Single(x => x.GetProperty("code").GetString() == "THN");
        panel.GetProperty("isPanel").GetBoolean().ShouldBeTrue();
        panel.GetProperty("children").EnumerateArray().Select(x => x.GetProperty("code").GetString()).ShouldBe(["UREA", "CREA"]);
        var child = panel.GetProperty("children")[0];
        child.GetProperty("name").GetString().ShouldBe("Urea Mẫu");
        child.GetProperty("groupName").GetString().ShouldBe("Hóa sinh");
        child.GetProperty("specimenName").GetString().ShouldBe("Huyết thanh (hóa sinh, miễn dịch)");
        child.GetProperty("unit").GetString().ShouldBe("mmol/L");
        child.GetProperty("valueType").GetString().ShouldBe("so");
        child.GetProperty("decimals").GetInt32().ShouldBe(1);
        child.GetProperty("autoApprove").GetBoolean().ShouldBeFalse();
        child.GetProperty("active").GetBoolean().ShouldBeTrue();
        child.GetProperty("rangeText").ValueKind.ShouldBe(JsonValueKind.Null);
        child.GetProperty("children").GetArrayLength().ShouldBe(0);
        child.EnumerateObject().Select(p => p.Name).Order().ShouldBe(
            ["active", "autoApprove", "children", "code", "decimals", "groupName", "id", "isPanel", "name", "rangeText", "specimenName", "unit", "valueType"]);
        // Dịch vụ ngừng dùng vẫn có trong danh sách.
        tree.EnumerateArray().Single(x => x.GetProperty("code").GetString() == "GLU").GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Khoang_tham_chieu_gop_de_xem_nhanh()
    {
        await using var s = await StartAsync("pkcrange");
        var instrument = await s.Scalar<Guid>("INSERT INTO instrument (name) VALUES ('Máy Thử') RETURNING id");
        await Create(s.Admin, Body(s.Serum, "A_SEX", "Theo giới Mẫu", b =>
        {
            b["decimals"] = 0;
            b["ranges"] = new[] { Range("M", low: 130, high: 170), Range("F", low: 120, high: 150) };
        }));
        await Create(s.Admin, Body(s.Serum, "B_TEXT", "Chữ Mẫu", b => b["ranges"] = new[] { Range(low: 1, high: 2, text: "Âm tính") }));
        await Create(s.Admin, Body(s.Serum, "C_DEC", "Hai số lẻ Mẫu", b => { b["decimals"] = 2; b["ranges"] = new[] { Range(low: 4, high: 10) }; }));
        await Create(s.Admin, Body(s.Serum, "D_LOW", "Chỉ thấp Mẫu", b => b["ranges"] = new[] { Range(low: 0.9m) }));
        await Create(s.Admin, Body(s.Serum, "E_HIGH", "Chỉ cao Mẫu", b => b["ranges"] = new[] { Range(high: 5.2m) }));
        await Create(s.Admin, Body(s.Serum, "F_AGE", "Theo tuổi Mẫu", b => b["ranges"] = new[] { Range(from: 0, to: 28, low: 1, high: 2) }));
        await Create(s.Admin, Body(s.Serum, "G_MACHINE", "Theo máy Mẫu", b => b["ranges"] = new[] { Range(instrument: instrument, low: 1, high: 2) }));
        await Create(s.Admin, Body(s.Serum, "H_NONE", "Không khoảng Mẫu"));

        var tree = await Tree(s.Admin);

        string? Text(string code) => tree.EnumerateArray().Single(x => x.GetProperty("code").GetString() == code).GetProperty("rangeText").GetString();
        Text("A_SEX").ShouldBe("Nam: 130 – 170 · Nữ: 120 – 150");
        Text("B_TEXT").ShouldBe("Âm tính");
        Text("C_DEC").ShouldBe("4.00 – 10.00");
        Text("D_LOW").ShouldBe("≥ 0.9");
        Text("E_HIGH").ShouldBe("≤ 5.2");
        Text("F_AGE").ShouldBe("Theo tuổi / máy");
        Text("G_MACHINE").ShouldBe("Theo tuổi / máy");
        Text("H_NONE").ShouldBeNull();
    }

    [Fact]
    public async Task Khoang_chi_co_chu_cach_trang_hoac_khong_so_thi_theo_tuoi_may()
    {
        await using var s = await StartAsync("pkcrange2");
        var id = await Create(s.Admin, Body(s.Serum, "X1", "Mẫu một"));
        // Dòng không có giá trị nào (CSDL cho phép, API không tạo được): gộp thành "Theo tuổi / máy" như NestJS.
        await s.Exec("INSERT INTO reference_range (service_id, text_range) VALUES (@id, '  ')", new { id });

        var tree = await Tree(s.Admin);

        tree[0].GetProperty("rangeText").GetString().ShouldBe("Theo tuổi / máy");
    }

    [Fact]
    public async Task Meta_loai_mau_theo_ten_nhom_dang_dung_va_moi_may()
    {
        await using var s = await StartAsync("pkcmeta");
        await Create(s.Admin, Body(s.Serum, "S1", "Mẫu một", b => b["groupName"] = "Miễn dịch"));
        await Create(s.Admin, Body(s.Serum, "S2", "Mẫu hai", b => b["groupName"] = "Hóa sinh"));
        await Create(s.Admin, Body(s.Serum, "S3", "Mẫu ba", b => b["groupName"] = "Hóa sinh"));
        await s.Exec("INSERT INTO instrument (name, active) VALUES ('Máy B Thử', true), ('Máy A Thử', false)");

        var response = await s.Tech.GetAsync("/api/catalog/meta", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var meta = await JsonOf(response);
        meta.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["groups", "instruments", "specimenTypes"]);
        meta.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["Hóa sinh", "Miễn dịch"]);
        meta.GetProperty("instruments").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["Máy A Thử", "Máy B Thử"]);
        var specimens = meta.GetProperty("specimenTypes").EnumerateArray().ToList();
        specimens.Count.ShouldBe(5);
        specimens.Select(x => x.GetProperty("name").GetString()).ShouldBe(specimens.Select(x => x.GetProperty("name").GetString()).Order(StringComparer.Ordinal).ToList()!);
        specimens.First(x => x.GetProperty("code").GetString() == "SERUM").GetProperty("id").GetString().ShouldBe(s.Serum.ToString());
        specimens[0].EnumerateObject().Select(p => p.Name).Order().ShouldBe(["code", "id", "name"]);
    }

    [Fact]
    public async Task Chi_tiet_tra_dung_truong_chi_so_con_khoang_va_luat()
    {
        await using var s = await StartAsync("pkcdetail");
        var instrument = await s.Scalar<Guid>("INSERT INTO instrument (name) VALUES ('Máy Thử') RETURNING id");
        var a = await Create(s.Admin, Body(s.Serum, "AAA", "Chỉ số A Mẫu"));
        var b2 = await Create(s.Admin, Body(s.Serum, "BBB", "Chỉ số B Mẫu"));
        var id = await Create(s.Admin, Body(s.Serum, "DT", "Định tính Mẫu", body =>
        {
            body["groupName"] = "Miễn dịch"; body["unit"] = "U/L"; body["valueType"] = "dinh-tinh"; body["printOrder"] = 7; body["hisCode"] = "HIS-1";
            body["ranges"] = new[]
            {
                Range("F", 0, 365, instrument, 1.5m, 2.5m, 1m, 3m, "chữ"),
                Range(low: 0.5m, high: 1),
            };
            body["qualitativeRules"] = new object[]
            {
                new { instrumentId = (Guid?)null, rawValue = "pos", displayValue = "Dương tính", flag = "Abnormal" },
                new { instrumentId = (Guid?)instrument, rawValue = "neg", displayValue = "Âm tính", flag = (string?)null },
            };
        }));
        var panel = await Create(s.Admin, Body(s.Serum, "GOI", "Gói Mẫu", body => { body["isPanel"] = true; body["childIds"] = new[] { b2, a }; }));

        var detail = await Detail(s.Tech, id);

        detail.EnumerateObject().Select(p => p.Name).Order().ShouldBe(
            ["active", "autoApprove", "children", "code", "decimals", "groupName", "hisCode", "id", "isPanel", "name", "printOrder",
             "qualitativeRules", "ranges", "specimenTypeId", "unit", "valueType"]);
        detail.GetProperty("id").GetString().ShouldBe(id.ToString());
        detail.GetProperty("code").GetString().ShouldBe("DT");
        detail.GetProperty("groupName").GetString().ShouldBe("Miễn dịch");
        detail.GetProperty("specimenTypeId").GetString().ShouldBe(s.Serum.ToString());
        detail.GetProperty("unit").GetString().ShouldBe("U/L");
        detail.GetProperty("valueType").GetString().ShouldBe("dinh-tinh");
        detail.GetProperty("decimals").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("printOrder").GetInt32().ShouldBe(7);
        detail.GetProperty("hisCode").GetString().ShouldBe("HIS-1");
        detail.GetProperty("children").GetArrayLength().ShouldBe(0);
        // Khoảng chung (máy null) đứng trước khoảng riêng của máy.
        var ranges = detail.GetProperty("ranges").EnumerateArray().ToList();
        ranges[0].GetProperty("low").GetDouble().ShouldBe(0.5);
        ranges[0].GetProperty("sex").ValueKind.ShouldBe(JsonValueKind.Null);
        ranges[0].GetProperty("criticalLow").ValueKind.ShouldBe(JsonValueKind.Null);
        ranges[1].GetProperty("sex").GetString().ShouldBe("F");
        ranges[1].GetProperty("ageFromDays").GetInt32().ShouldBe(0);
        ranges[1].GetProperty("ageToDays").GetInt32().ShouldBe(365);
        ranges[1].GetProperty("instrumentId").GetString().ShouldBe(instrument.ToString());
        ranges[1].GetProperty("high").GetDouble().ShouldBe(2.5);
        ranges[1].GetProperty("criticalLow").GetDouble().ShouldBe(1);
        ranges[1].GetProperty("criticalHigh").GetDouble().ShouldBe(3);
        ranges[1].GetProperty("textRange").GetString().ShouldBe("chữ");
        ranges[1].EnumerateObject().Select(p => p.Name).Order().ShouldBe(
            ["ageFromDays", "ageToDays", "criticalHigh", "criticalLow", "high", "instrumentId", "low", "sex", "textRange"]);
        var rules = detail.GetProperty("qualitativeRules").EnumerateArray().ToList();
        rules.Select(r => r.GetProperty("rawValue").GetString()).ShouldBe(["neg", "pos"]);
        rules[1].GetProperty("displayValue").GetString().ShouldBe("Dương tính");
        rules[1].GetProperty("flag").GetString().ShouldBe("Abnormal");
        rules[1].GetProperty("instrumentId").ValueKind.ShouldBe(JsonValueKind.Null);
        rules[0].GetProperty("instrumentId").GetString().ShouldBe(instrument.ToString());
        rules[0].EnumerateObject().Select(p => p.Name).Order().ShouldBe(["displayValue", "flag", "instrumentId", "rawValue"]);

        var panelDetail = await Detail(s.Admin, panel);
        var children = panelDetail.GetProperty("children").EnumerateArray().ToList();
        children.Select(c => c.GetProperty("code").GetString()).ShouldBe(["BBB", "AAA"]);
        children[0].EnumerateObject().Select(p => p.Name).Order().ShouldBe(["code", "id", "name"]);
    }

    [Fact]
    public async Task Chi_tiet_khong_co_hoac_id_hong_hoac_phong_kham_khac()
    {
        await using var s = await StartAsync("pkcnf");
        await using var other = await StartAsync("pkcnf2");
        var id = await Create(s.Admin, Body(s.Serum));

        await ShouldFail(await s.Admin.GetAsync($"/api/catalog/services/{Guid.NewGuid()}", Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        await ShouldFail(await s.Admin.GetAsync("/api/catalog/services/khong-phai-uuid", Ct), HttpStatusCode.BadRequest, "Validation failed (uuid is expected)");
        await ShouldFail(await other.Admin.GetAsync($"/api/catalog/services/{id}", Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        await ShouldFail(await other.Admin.PutAsJsonAsync($"/api/catalog/services/{id}", Body(other.Serum), Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        await ShouldFail(await other.Admin.DeleteAsync($"/api/catalog/services/{id}", Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        (await Tree(other.Admin)).GetArrayLength().ShouldBe(0);
        (await Detail(s.Admin, id)).GetProperty("code").GetString().ShouldBe("GLU");
    }

    [Fact]
    public async Task Tao_dich_vu_cat_khoang_trang_va_luu_mac_dinh()
    {
        await using var s = await StartAsync("pkccreate");

        var id = await Create(s.Admin, Body(s.Serum, "GLU", "  Glucose Mẫu ", b =>
        {
            b["groupName"] = "  "; b["unit"] = " mmol/L "; b["decimals"] = 2; b["hisCode"] = "   "; b["autoApprove"] = true;
        }));

        var detail = await Detail(s.Admin, id);
        detail.GetProperty("code").GetString().ShouldBe("GLU");
        detail.GetProperty("name").GetString().ShouldBe("Glucose Mẫu");
        detail.GetProperty("groupName").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("unit").GetString().ShouldBe("mmol/L");
        detail.GetProperty("decimals").GetInt32().ShouldBe(2);
        detail.GetProperty("printOrder").GetInt32().ShouldBe(0);
        detail.GetProperty("hisCode").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("autoApprove").GetBoolean().ShouldBeTrue();
        detail.GetProperty("active").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Goi_khong_giu_don_vi_so_le_tu_duyet_khoang_va_luat()
    {
        await using var s = await StartAsync("pkcpanel");
        var child = await Create(s.Admin, Body(s.Serum, "C1", "Con Mẫu"));

        var id = await Create(s.Admin, Body(s.Serum, "GOI", "Gói Mẫu", b =>
        {
            b["isPanel"] = true; b["unit"] = "mg"; b["decimals"] = 3; b["autoApprove"] = true; b["valueType"] = "dinh-tinh"; b["childIds"] = new[] { child, child };
            b["ranges"] = new[] { Range(low: 1, high: 2) };
            b["qualitativeRules"] = new[] { new { instrumentId = (Guid?)null, rawValue = "pos", displayValue = "Dương", flag = (string?)null } };
        }));

        var detail = await Detail(s.Admin, id);
        detail.GetProperty("isPanel").GetBoolean().ShouldBeTrue();
        detail.GetProperty("unit").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("decimals").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("autoApprove").GetBoolean().ShouldBeFalse();
        detail.GetProperty("ranges").GetArrayLength().ShouldBe(0);
        detail.GetProperty("qualitativeRules").GetArrayLength().ShouldBe(0);
        detail.GetProperty("children").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Luat_dinh_tinh_chi_luu_khi_kieu_ket_qua_la_dinh_tinh()
    {
        await using var s = await StartAsync("pkcrules");
        var rule = new[] { new { instrumentId = (Guid?)null, rawValue = " pos ", displayValue = " Dương tính ", flag = "Abnormal" } };

        var numeric = await Create(s.Admin, Body(s.Serum, "SO", "Số Mẫu", b => b["qualitativeRules"] = rule));
        var qualitative = await Create(s.Admin, Body(s.Serum, "DT", "Định tính Mẫu", b => { b["valueType"] = "dinh-tinh"; b["qualitativeRules"] = rule; }));

        (await Detail(s.Admin, numeric)).GetProperty("qualitativeRules").GetArrayLength().ShouldBe(0);
        var saved = (await Detail(s.Admin, qualitative)).GetProperty("qualitativeRules")[0];
        saved.GetProperty("rawValue").GetString().ShouldBe("pos");
        saved.GetProperty("displayValue").GetString().ShouldBe("Dương tính");
    }

    [Theory]
    [InlineData("code", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("code", "", "Nhập mã dịch vụ")]
    [InlineData("code", "GL U", "Mã dịch vụ không được có khoảng trắng")]
    [InlineData("name", "", "Nhập tên dịch vụ")]
    [InlineData("specimenTypeId", null, "Chọn loại mẫu")]
    [InlineData("specimenTypeId", "khong-phai-uuid", "Chọn loại mẫu")]
    [InlineData("valueType", "khac", "Kiểu kết quả không hợp lệ")]
    [InlineData("decimals", 7, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("decimals", -1, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("printOrder", 100001, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("isPanel", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("autoApprove", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("active", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("childIds", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("ranges", null, "Dữ liệu gửi lên không hợp lệ")]
    [InlineData("qualitativeRules", null, "Dữ liệu gửi lên không hợp lệ")]
    public async Task Tao_dich_vu_kiem_dau_vao(string field, object? value, string message)
    {
        await using var s = await StartAsync($"pkcv{Interlocked.Increment(ref _validationCase)}");

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b => b[field] = value), Ct), HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Tao_dich_vu_qua_dai_hoac_qua_nhieu_dong_thi_400()
    {
        await using var s = await StartAsync("pkcmax");
        const string Message = "Dữ liệu gửi lên không hợp lệ";

        async Task Fails(Action<Dictionary<string, object?>> edit) =>
            await ShouldFail(await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: edit), Ct), HttpStatusCode.BadRequest, Message);

        await Fails(b => b["code"] = new string('A', 65));
        await Fails(b => b["name"] = new string('A', 501));
        await Fails(b => b["groupName"] = new string('A', 101));
        await Fails(b => b["unit"] = new string('A', 51));
        await Fails(b => b["hisCode"] = new string('A', 65));
        await Fails(b => b["childIds"] = Enumerable.Range(0, 301).Select(_ => Guid.NewGuid()).ToArray());
        await Fails(b => b["childIds"] = new[] { "khong-phai-uuid" });
        await Fails(b => b["ranges"] = Enumerable.Range(0, 101).Select(_ => Range(low: 1)).ToArray());
        await Fails(b => b["qualitativeRules"] = Enumerable.Range(0, 101).Select(i => new { rawValue = $"r{i}", displayValue = "x" }).ToArray());
        await Fails(b => b["ranges"] = new[] { Range(from: 60001, low: 1) });
        await Fails(b => b["ranges"] = new[] { Range(low: 1, text: new string('A', 201)) });
    }

    [Fact]
    public async Task Khoang_va_luat_sai_dinh_dang_thi_400_voi_cau_rieng()
    {
        await using var s = await StartAsync("pkcrdto");
        async Task Fails(string field, object value, string message) =>
            await ShouldFail(await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b => b[field] = value), Ct), HttpStatusCode.BadRequest, message);

        await Fails("ranges", new[] { new { sex = "O", low = 1 } }, "Giới tính không hợp lệ");
        await Fails("qualitativeRules", new[] { new { rawValue = "", displayValue = "x" } }, "Nhập giá trị máy gửi");
        await Fails("qualitativeRules", new[] { new { rawValue = "pos", displayValue = "" } }, "Nhập chữ hiển thị");
        await Fails("qualitativeRules", new[] { new { rawValue = "pos", displayValue = "x", flag = "Normal" } }, "Cờ không hợp lệ");
    }

    [Fact]
    public async Task Kiem_khoang_tham_chieu_gop_cac_loi_theo_dong()
    {
        await using var s = await StartAsync("pkcvalid");

        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b => b["ranges"] = new[]
            {
                Range(low: 5, high: 1),
                Range(low: 1, high: 2, criticalLow: 3, criticalHigh: 1.5m, from: 10, to: 5),
                Range(),
                Range(text: "   "),
                Range(low: 1),
            }), Ct),
            HttpStatusCode.BadRequest,
            "Khoảng tham chiếu dòng 1: giá trị thấp phải nhỏ hơn hoặc bằng giá trị cao; " +
            "Khoảng tham chiếu dòng 2: nguy hiểm thấp phải nhỏ hơn hoặc bằng giá trị thấp; " +
            "Khoảng tham chiếu dòng 2: giá trị cao phải nhỏ hơn hoặc bằng nguy hiểm cao; " +
            "Khoảng tham chiếu dòng 2: tuổi từ phải nhỏ hơn hoặc bằng tuổi đến; " +
            "Khoảng tham chiếu dòng 3: nhập ít nhất một giá trị hoặc chữ tham chiếu; " +
            "Khoảng tham chiếu dòng 4: nhập ít nhất một giá trị hoặc chữ tham chiếu");
        (await Tree(s.Admin)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Luat_dinh_tinh_lap_gia_tri_may_gui_thi_400()
    {
        await using var s = await StartAsync("pkcdup");
        var instrument = await s.Scalar<Guid>("INSERT INTO instrument (name) VALUES ('Máy Thử') RETURNING id");

        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b =>
            {
                b["valueType"] = "dinh-tinh";
                b["qualitativeRules"] = new object[]
                {
                    new { instrumentId = (Guid?)null, rawValue = "Pos", displayValue = "Dương" },
                    new { instrumentId = (Guid?)null, rawValue = " pos ", displayValue = "Dương 2" },
                };
            }), Ct),
            HttpStatusCode.BadRequest, "Luật định tính bị lặp giá trị máy gửi \"pos\"");

        // Cùng giá trị nhưng khác máy thì không lặp.
        await Create(s.Admin, Body(s.Serum, edit: b =>
        {
            b["valueType"] = "dinh-tinh";
            b["qualitativeRules"] = new object[]
            {
                new { instrumentId = (Guid?)null, rawValue = "pos", displayValue = "Dương" },
                new { instrumentId = (Guid?)instrument, rawValue = "pos", displayValue = "Dương" },
            };
        }));
    }

    [Fact]
    public async Task Ma_trung_thi_409_ca_khi_tao_va_khi_sua()
    {
        await using var s = await StartAsync("pkcdupcode");
        await Create(s.Admin, Body(s.Serum, "GLU"));
        var other = await Create(s.Admin, Body(s.Serum, "UREA", "Urea Mẫu"));

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, "GLU"), Ct), HttpStatusCode.Conflict, "Mã dịch vụ đã tồn tại");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/catalog/services/{other}", Body(s.Serum, "GLU"), Ct), HttpStatusCode.Conflict, "Mã dịch vụ đã tồn tại");
        (await Detail(s.Admin, other)).GetProperty("code").GetString().ShouldBe("UREA");
    }

    [Fact]
    public async Task Loai_mau_may_hoac_chi_so_con_khong_ton_tai_thi_400_va_khong_luu_gi()
    {
        await using var s = await StartAsync("pkcfk");
        const string Message = "Loại mẫu, máy hoặc chỉ số con không tồn tại";

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(Guid.NewGuid()), Ct), HttpStatusCode.BadRequest, Message);
        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b => b["ranges"] = new[] { Range(instrument: Guid.NewGuid(), low: 1) }), Ct),
            HttpStatusCode.BadRequest, Message);
        await ShouldFail(
            await s.Admin.PostAsJsonAsync("/api/catalog/services", Body(s.Serum, edit: b => { b["isPanel"] = true; b["childIds"] = new[] { Guid.NewGuid() }; }), Ct),
            HttpStatusCode.BadRequest, Message);

        (await Tree(s.Admin)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Sua_dich_vu_thay_toan_bo_chi_so_con_khoang_va_luat_204()
    {
        await using var s = await StartAsync("pkcupdate");
        var a = await Create(s.Admin, Body(s.Serum, "AAA", "A Mẫu"));
        var b2 = await Create(s.Admin, Body(s.Serum, "BBB", "B Mẫu"));
        var id = await Create(s.Admin, Body(s.Serum, "GOI", "Gói Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { a, b2 }; }));

        var response = await s.Admin.PutAsJsonAsync($"/api/catalog/services/{id}", Body(s.Serum, "GOI2", "Gói Mẫu 2", b =>
        {
            b["isPanel"] = true; b["groupName"] = "Nhóm"; b["childIds"] = new[] { b2 };
        }), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var detail = await Detail(s.Admin, id);
        detail.GetProperty("code").GetString().ShouldBe("GOI2");
        detail.GetProperty("groupName").GetString().ShouldBe("Nhóm");
        detail.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldBe(["BBB"]);

        // Đổi gói thành chỉ số thường: bỏ chỉ số con, nhận khoảng tham chiếu.
        (await s.Admin.PutAsJsonAsync($"/api/catalog/services/{id}", Body(s.Serum, "GOI2", "Gói Mẫu 2", b => b["ranges"] = new[] { Range(low: 1, high: 2) }), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        detail = await Detail(s.Admin, id);
        detail.GetProperty("children").GetArrayLength().ShouldBe(0);
        detail.GetProperty("ranges").GetArrayLength().ShouldBe(1);
        (await Detail(s.Admin, b2)).GetProperty("code").GetString().ShouldBe("BBB");
    }

    [Fact]
    public async Task Sua_dich_vu_khong_co_thi_404_va_id_hong_thi_400()
    {
        await using var s = await StartAsync("pkcupd404");

        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/catalog/services/{Guid.NewGuid()}", Body(s.Serum), Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        await ShouldFail(await s.Admin.PutAsJsonAsync("/api/catalog/services/abc", Body(s.Serum), Ct), HttpStatusCode.BadRequest, "Validation failed (uuid is expected)");
    }

    [Fact]
    public async Task Chi_so_con_khong_duoc_la_goi_khong_lenh_nhieu_cap_khong_tu_lam_con()
    {
        await using var s = await StartAsync("pkcnest");
        var leaf = await Create(s.Admin, Body(s.Serum, "LEAF", "Lá Mẫu"));
        var inner = await Create(s.Admin, Body(s.Serum, "IN", "Gói trong Mẫu", b => b["isPanel"] = true));
        var outer = await Create(s.Admin, Body(s.Serum, "OUT", "Gói ngoài Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { leaf }; }));

        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/catalog/services/{outer}", Body(s.Serum, "OUT", "Gói ngoài Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { inner }; }), Ct),
            HttpStatusCode.BadRequest, "Chỉ số con không được là một gói khác: Gói trong Mẫu");
        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/catalog/services/{outer}", Body(s.Serum, "OUT", "Gói ngoài Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { outer }; }), Ct),
            HttpStatusCode.BadRequest, "Dịch vụ không thể là con của chính nó");
        await ShouldFail(
            await s.Admin.PutAsJsonAsync($"/api/catalog/services/{leaf}", Body(s.Serum, "LEAF", "Lá Mẫu", b => b["isPanel"] = true), Ct),
            HttpStatusCode.BadRequest, "Dịch vụ này đang là chỉ số con của gói \"Gói ngoài Mẫu\" nên không thể thành gói");
        // Lỗi thì không đổi gì.
        (await Detail(s.Admin, outer)).GetProperty("children").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldBe(["LEAF"]);
    }

    [Fact]
    public async Task Xoa_dich_vu_chua_dung_204_kem_khoang_luat_va_khai_bao_may()
    {
        await using var s = await StartAsync("pkcdel");
        var instrument = await s.Scalar<Guid>("INSERT INTO instrument (name) VALUES ('Máy Thử') RETURNING id");
        var id = await Create(s.Admin, Body(s.Serum, "DT", "Định tính Mẫu", b =>
        {
            b["valueType"] = "dinh-tinh"; b["ranges"] = new[] { Range(low: 1) };
            b["qualitativeRules"] = new[] { new { instrumentId = (Guid?)null, rawValue = "pos", displayValue = "Dương" } };
        }));
        await s.Exec("INSERT INTO instrument_service (instrument_id, service_id) VALUES (@instrument, @id)", new { instrument, id });
        var panel = await Create(s.Admin, Body(s.Serum, "GOI", "Gói Mẫu", b => b["isPanel"] = true));
        var leaf = await Create(s.Admin, Body(s.Serum, "LA", "Lá Mẫu"));
        await Create(s.Admin, Body(s.Serum, "GOI2", "Gói hai Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { leaf }; }));

        (await s.Admin.DeleteAsync($"/api/catalog/services/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // Xoá gói thì chỉ số con vẫn còn.
        (await s.Admin.DeleteAsync($"/api/catalog/services/{panel}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldFail(await s.Admin.GetAsync($"/api/catalog/services/{id}", Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        (await s.Scalar<int>("SELECT count(*)::int FROM reference_range")).ShouldBe(0);
        (await s.Scalar<int>("SELECT count(*)::int FROM qualitative_rule")).ShouldBe(0);
        (await s.Scalar<int>("SELECT count(*)::int FROM instrument_service")).ShouldBe(0);
        (await Tree(s.Admin)).EnumerateArray().Select(x => x.GetProperty("code").GetString()).ShouldBe(["GOI2"]);
    }

    [Fact]
    public async Task Xoa_dich_vu_khong_co_thi_404()
    {
        await using var s = await StartAsync("pkcdel404");

        await ShouldFail(await s.Admin.DeleteAsync($"/api/catalog/services/{Guid.NewGuid()}", Ct), HttpStatusCode.NotFound, "Không tìm thấy dịch vụ");
        await ShouldFail(await s.Admin.DeleteAsync("/api/catalog/services/abc", Ct), HttpStatusCode.BadRequest, "Validation failed (uuid is expected)");
    }

    [Fact]
    public async Task Khong_xoa_duoc_dich_vu_dang_co_trong_chi_dinh()
    {
        await using var s = await StartAsync("pkcdeluse");
        var id = await Create(s.Admin, Body(s.Serum, "GLU"));
        var patient = await s.Scalar<Guid>("INSERT INTO patient (code, full_name, sex) VALUES ('BN1', 'Bệnh nhân Thử', 'M') RETURNING id");
        var order = await s.Scalar<Guid>("INSERT INTO lab_order (patient_id) VALUES (@patient) RETURNING id", new { patient });
        var sample = await s.Scalar<Guid>(
            "INSERT INTO sample (order_id, barcode, specimen_type_id) VALUES (@order, '1001', @serum) RETURNING id", new { order, serum = s.Serum });
        await s.Exec("INSERT INTO order_item (order_id, sample_id, service_id) VALUES (@order, @sample, @id), (@order, @sample, @id)", new { order, sample, id });

        await ShouldFail(
            await s.Admin.DeleteAsync($"/api/catalog/services/{id}", Ct), HttpStatusCode.Conflict,
            "Dịch vụ đã có trong 2 dòng chỉ định nên không xoá được — hãy chuyển sang \"Ngừng dùng\"");
        (await Detail(s.Admin, id)).GetProperty("code").GetString().ShouldBe("GLU");
    }

    [Fact]
    public async Task Khong_xoa_duoc_chi_so_con_cua_goi_hoac_dang_map_kenh_may()
    {
        await using var s = await StartAsync("pkcdelref");
        var instrument = await s.Scalar<Guid>("INSERT INTO instrument (name) VALUES ('Máy Thử') RETURNING id");
        var leaf = await Create(s.Admin, Body(s.Serum, "LA", "Lá Mẫu"));
        await Create(s.Admin, Body(s.Serum, "GA", "Gói A Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { leaf }; }));
        await Create(s.Admin, Body(s.Serum, "GB", "Gói B Mẫu", b => { b["isPanel"] = true; b["childIds"] = new[] { leaf }; }));

        await ShouldFail(
            await s.Admin.DeleteAsync($"/api/catalog/services/{leaf}", Ct), HttpStatusCode.Conflict,
            "Đang là chỉ số con của gói \"Gói A Mẫu\", \"Gói B Mẫu\" — gỡ khỏi gói trước khi xoá");

        var mapped = await Create(s.Admin, Body(s.Serum, "MAP", "Đã map Mẫu"));
        await s.Exec("INSERT INTO instrument_channel (instrument_id, channel_code, service_id) VALUES (@instrument, 'CH1', @mapped)", new { instrument, mapped });
        await ShouldFail(
            await s.Admin.DeleteAsync($"/api/catalog/services/{mapped}", Ct), HttpStatusCode.Conflict,
            "Đang được map kênh ở máy Máy Thử — xoá kênh đó trước khi xoá dịch vụ");
    }

    [Fact]
    public async Task Truong_la_trong_than_request_thi_400()
    {
        await using var s = await StartAsync("pkcextra");

        var body = Body(s.Serum, edit: b => b["laLam"] = 1);

        (await s.Admin.PostAsJsonAsync("/api/catalog/services", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
