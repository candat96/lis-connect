using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Server.Tenancy;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Tiếp nhận tối giản trên <c>/api/reception</c>: tìm người bệnh, tạo chỉ định, mẫu và barcode. Mọi dữ liệu là dữ liệu giả.</summary>
public sealed class ReceptionApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Env : IAsyncDisposable
    {
        public ApiHost Host { get; init; } = null!;

        public Clinic Clinic { get; init; } = null!;

        public Db Db => Host.TenantOf(Clinic).Db;

        public Guid Wbc { get; init; }

        public Guid Hgb { get; init; }

        public Guid Glucose { get; init; }

        public Guid Retired { get; init; }

        public Guid Panel { get; init; }

        public Task<HttpClient> LoginAsync(string user) => Host.LoginAsync(Clinic.Code, user, Password);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private async Task<Env> CreateAsync(string code)
    {
        var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync(code, $"Phòng khám Thử {code}");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        await host.AddUserAsync(clinic, "tiepnhan", Password, "tiep-nhan");

        var catalog = host.TenantOf(clinic).Catalog;
        var types = await catalog.SpecimenTypesAsync();
        var edta = types.First(s => s.Code == "EDTA").Id;
        var serum = types.First(s => s.Code == "SERUM").Id;

        Task<Guid> Save(string serviceCode, string name, Guid specimen, bool panel = false, bool active = true) =>
            catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, serviceCode, name, null, specimen, null, "so", 1, 0, panel, false, active, null, []));

        var wbc = await Save("WBC", "Bạch cầu Mẫu", edta);
        var hgb = await Save("HGB", "Huyết sắc tố Mẫu", edta);
        var glucose = await Save("GLU", "Đường huyết Mẫu", serum);
        var retired = await Save("CU", "Dịch vụ cũ Mẫu", serum, active: false);
        var panel = await Save("CBC", "Công thức máu Mẫu", edta, panel: true);
        await catalog.SetChildrenAsync(panel, [wbc, hgb]);

        return new Env { Host = host, Clinic = clinic, Wbc = wbc, Hgb = hgb, Glucose = glucose, Retired = retired, Panel = panel };
    }

    private static object NewPatient(string name = "Nguyễn Văn Thử") => new { fullName = name, birthYear = 1985, sex = "M", phone = "0900000001" };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task Tiep_nhan_nguoi_benh_moi_sinh_ma_va_barcode()
    {
        await using var env = await CreateAsync("e1a");
        var client = await env.LoginAsync("tiepnhan");

        var response = await client.PostAsJsonAsync("/api/reception/orders",
            new { patient = NewPatient(), serviceIds = new[] { env.Wbc, env.Glucose }, orderingDoctor = "BS Thử", diagnosis = "Khám thử" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await Json(response);
        body.GetProperty("patientCode").GetString()!.ShouldStartWith("BN");
        var samples = body.GetProperty("samples").EnumerateArray().ToList();
        samples.Count.ShouldBe(2);
        samples.Select(s => s.GetProperty("barcode").GetString()).Distinct().Count().ShouldBe(2);
        samples.Select(s => s.GetProperty("specimenName").GetString()).ShouldBe(["Máu EDTA (huyết học)", "Huyết thanh (hóa sinh, miễn dịch)"], ignoreOrder: true);
        samples.Single(s => s.GetProperty("specimenName").GetString()!.StartsWith("Máu"))
            .GetProperty("serviceNames").EnumerateArray().Select(n => n.GetString()).ShouldBe(["Bạch cầu Mẫu"]);

        await using var connection = await env.Db.OpenAsync();
        (await connection.ExecuteScalarAsync<string>("SELECT barcode_mode FROM facility WHERE id = 1")).ShouldBe("per-specimen");
        (await connection.ExecuteScalarAsync<string>("SELECT ordering_doctor FROM lab_order")).ShouldBe("BS Thử");
    }

    [Fact]
    public async Task Tiep_nhan_panel_tach_chi_so_con()
    {
        await using var env = await CreateAsync("e1b");
        var client = await env.LoginAsync("tiepnhan");

        var response = await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient(), serviceIds = new[] { env.Panel } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var samples = (await Json(response)).GetProperty("samples").EnumerateArray().ToList();
        samples.Count.ShouldBe(1);
        samples[0].GetProperty("serviceNames").GetArrayLength().ShouldBe(2);

        var detail = await Json(await client.GetAsync($"/api/samples/{samples[0].GetProperty("sampleId").GetGuid()}", Ct));
        detail.GetProperty("barcode").GetString().ShouldBe(samples[0].GetProperty("barcode").GetString());
        var codes = detail.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("serviceCode").GetString()).ToList();
        codes.ShouldContain("CBC");
        codes.ShouldContain("WBC");
        codes.ShouldContain("HGB");
    }

    [Fact]
    public async Task Tiep_nhan_nguoi_benh_co_san_khong_tao_moi()
    {
        await using var env = await CreateAsync("e1c");
        var client = await env.LoginAsync("tiepnhan");
        var first = await Json(await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient("Trần Thị Mẫu"), serviceIds = new[] { env.Wbc } }, Ct));

        var hits = await Json(await client.GetAsync("/api/reception/patients?q=%20tran%20thi%20", Ct));
        hits.GetArrayLength().ShouldBe(1);
        hits[0].GetProperty("code").GetString().ShouldBe(first.GetProperty("patientCode").GetString());
        hits[0].GetProperty("birthYear").GetInt32().ShouldBe(1985);
        hits[0].GetProperty("sex").GetString().ShouldBe("M");

        var second = await client.PostAsJsonAsync("/api/reception/orders",
            new { patientId = hits[0].GetProperty("id").GetGuid(), serviceIds = new[] { env.Hgb } }, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Json(second)).GetProperty("patientId").GetGuid().ShouldBe(first.GetProperty("patientId").GetGuid());
        await using var connection = await env.Db.OpenAsync();
        (await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM patient")).ShouldBe(1);

        // q dưới 2 ký tự (sau khi bỏ khoảng trắng) thì trả mảng rỗng.
        (await Json(await client.GetAsync("/api/reception/patients?q=%20t%20", Ct))).GetArrayLength().ShouldBe(0);
        (await Json(await client.GetAsync("/api/reception/patients", Ct))).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Muoi_yeu_cau_song_song_khong_trung_barcode()
    {
        await using var env = await CreateAsync("e1d");
        var client = await env.LoginAsync("tiepnhan");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            client.PostAsJsonAsync("/api/reception/orders",
                new { patient = NewPatient($"Người Thử {i}"), serviceIds = new[] { env.Wbc, env.Glucose } }, Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var bodies = await Task.WhenAll(responses.Select(Json));
        var barcodes = bodies.SelectMany(b => b.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("barcode").GetString())).ToList();
        barcodes.Count.ShouldBe(20);
        barcodes.Distinct().Count().ShouldBe(20);
        var codes = bodies.Select(b => b.GetProperty("patientCode").GetString()).ToList();
        codes.Distinct().Count().ShouldBe(10);
    }

    [Fact]
    public async Task Ky_thuat_vien_bi_403()
    {
        await using var env = await CreateAsync("e1e");
        var client = await env.LoginAsync("ktv");

        var order = await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient(), serviceIds = new[] { env.Wbc } }, Ct);
        var find = await client.GetAsync("/api/reception/patients?q=thu", Ct);

        foreach (var response in new[] { order, find })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Json(response)).GetProperty("message").GetString().ShouldBe("Tài khoản không có quyền tiếp nhận");
        }

    }

    [Fact]
    public async Task Tiep_nhan_va_quan_tri_duoc()
    {
        await using var env = await CreateAsync("e1f");

        foreach (var user in new[] { "tiepnhan", "admin" })
        {
            var client = await env.LoginAsync(user);
            (await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient(), serviceIds = new[] { env.Wbc } }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            (await client.GetAsync("/api/reception/patients?q=thu", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await env.Host.Anonymous.GetAsync("/api/reception/patients?q=thu", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dich_vu_ngung_dung_hoac_khong_co_bao_400()
    {
        await using var env = await CreateAsync("e1g");
        var client = await env.LoginAsync("tiepnhan");

        async Task Bad(object body, string message)
        {
            var response = await client.PostAsJsonAsync("/api/reception/orders", body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("message").GetString().ShouldBe(message);
        }

        await Bad(new { patient = NewPatient(), serviceIds = new[] { env.Retired } }, "Dịch vụ 'Dịch vụ cũ Mẫu' (CU) đã ngừng dùng");
        await Bad(new { patient = NewPatient(), serviceIds = new[] { Guid.NewGuid() } }, "Dịch vụ không tồn tại trong danh mục");
        await Bad(new { patient = NewPatient(), serviceIds = Array.Empty<Guid>() }, "Chưa chọn dịch vụ nào");
        await Bad(new { patient = NewPatient(), serviceIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() }, "Tối đa 50 dịch vụ mỗi chỉ định");
        await Bad(new { patient = NewPatient(), serviceIds = new[] { env.Wbc, env.Wbc } }, "Dịch vụ 'Bạch cầu Mẫu' đã được chọn hai lần");
        await Bad(new { serviceIds = new[] { env.Wbc } }, "Phải chọn người bệnh có sẵn hoặc nhập người bệnh mới, không được cả hai");
        await Bad(new { patientId = Guid.NewGuid(), patient = NewPatient(), serviceIds = new[] { env.Wbc } }, "Phải chọn người bệnh có sẵn hoặc nhập người bệnh mới, không được cả hai");
        await Bad(new { patientId = Guid.NewGuid(), serviceIds = new[] { env.Wbc } }, "Không tìm thấy người bệnh");
        await Bad(new { patient = new { fullName = "  ", birthYear = 1985, sex = "M" }, serviceIds = new[] { env.Wbc } }, "Chưa nhập họ tên người bệnh");
        await Bad(new { patient = new { fullName = new string('A', 201), birthYear = 1985, sex = "M" }, serviceIds = new[] { env.Wbc } }, "Họ tên tối đa 200 ký tự");
        await Bad(new { patient = new { fullName = "Thử", sex = "M" }, serviceIds = new[] { env.Wbc } }, "Phải nhập ngày sinh hoặc năm sinh");
        await Bad(new { patient = new { fullName = "Thử", birthYear = 1899, sex = "M" }, serviceIds = new[] { env.Wbc } }, $"Năm sinh phải từ 1900 đến {DateTime.UtcNow.Year}");
        await Bad(new { patient = new { fullName = "Thử", birthYear = DateTime.UtcNow.Year + 1, sex = "M" }, serviceIds = new[] { env.Wbc } }, $"Năm sinh phải từ 1900 đến {DateTime.UtcNow.Year}");
        await Bad(new { patient = new { fullName = "Thử", birthYear = 1985, sex = "X" }, serviceIds = new[] { env.Wbc } }, "Giới tính phải là M, F hoặc O");

        await using var connection = await env.Db.OpenAsync();
        (await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM lab_order")).ShouldBe(0);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM patient")).ShouldBe(0);

        (await client.PostAsJsonAsync("/api/reception/orders",
            new { patient = new { fullName = "Thử", dob = "1985-03-12", sex = "F" }, serviceIds = new[] { env.Wbc } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Phong_kham_moi_tao_tu_web_co_facility_mac_dinh()
    {
        await using var host = await ApiHost.StartAsync(fixture);

        var clinic = await host.Provisioner.CreateAsync("e1h", "Phòng khám Thử web", "admin", "Quản trị Thử", Password);

        await using var connection = await host.TenantOf(clinic).Db.OpenAsync();
        var row = await connection.QuerySingleAsync<(string Name, string Prefix, string Mode)>(
            "SELECT name, barcode_prefix, barcode_mode FROM facility WHERE id = 1");
        row.Name.ShouldBe("Phòng khám Thử web");
        row.Mode.ShouldBe("per-specimen");

        var client = await host.LoginAsync("e1h", "admin", Password);
        var catalog = host.TenantOf(clinic).Catalog;
        var edta = (await catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var wbc = await catalog.SaveServiceAsync(new ServiceInfo(Guid.Empty, "WBC", "Bạch cầu Mẫu", null, edta, null, "so", 1, 0, false, false, true, null, []));
        (await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient(), serviceIds = new[] { wbc } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Facility_san_co_khong_bi_ghi_de()
    {
        await using var env = await CreateAsync("e1i");
        await new FacilityStore(env.Db).SaveAsync(new FacilityInfo("Phòng xét nghiệm Thử cài sẵn", "Địa chỉ Thử", null, null, "7", "yyMMdd", 4, "single"));
        var client = await env.LoginAsync("tiepnhan");

        var response = await client.PostAsJsonAsync("/api/reception/orders", new { patient = NewPatient(), serviceIds = new[] { env.Wbc, env.Glucose } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var samples = (await Json(response)).GetProperty("samples").EnumerateArray().ToList();
        samples.Select(s => s.GetProperty("barcode").GetString()).Distinct().Single()!.ShouldStartWith("7");
        var facility = (await new FacilityStore(env.Db).GetAsync())!;
        facility.Name.ShouldBe("Phòng xét nghiệm Thử cài sẵn");
        facility.BarcodePrefix.ShouldBe("7");
        facility.BarcodeSeqDigits.ShouldBe(4);
        facility.BarcodeMode.ShouldBe("single");

        // Chạy lại việc bảo đảm dòng mặc định cũng không ghi đè.
        await new FacilityStore(env.Db).EnsureDefaultAsync("Tên khác Thử");
        (await new FacilityStore(env.Db).GetAsync())!.Name.ShouldBe("Phòng xét nghiệm Thử cài sẵn");
    }
}
