using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Server.Api;
using Lis.Server.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>
/// Tạo phòng khám không được đè lên CSDL có sẵn: web từ chối CSDL đã tồn tại và CSDL master; dòng lệnh <c>clinic add --db</c>
/// vẫn dùng lại được CSDL có sẵn (chuyển từ bản cài tại chỗ) nhưng từ chối master và tên CSDL không hợp lệ.
/// </summary>
public sealed class ProvisioningSafetyTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Key = "khoa-thiet-lap-thu-du-dai";
    private const string Password = "mat-khau-thu-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string code) => new
    {
        setupKey = Key, code, name = "Phòng khám Mẫu", adminUsername = "admin", adminFullName = "Quản trị Thử", adminPassword = Password,
    };

    private string ConnectionTo(string database) =>
        new NpgsqlConnectionStringBuilder(fixture.MasterConnection) { Database = database }.ConnectionString;

    private async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var connection = new NpgsqlConnection(ConnectionTo(database));
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<bool> DatabaseExistsAsync(string dbName)
    {
        await using var connection = await OpenAsync("postgres");
        return await connection.ExecuteScalarAsync<int?>("SELECT 1 FROM pg_database WHERE datname = @dbName", new { dbName }) is not null;
    }

    private async Task<bool> TableExistsAsync(string database, string table)
    {
        await using var connection = await OpenAsync(database);
        return await connection.ExecuteScalarAsync<bool>("SELECT to_regclass(@name) IS NOT NULL", new { name = "public." + table });
    }

    /// <summary>CSDL có sẵn, chỉ chứa bảng <c>du_lieu_cu</c> với một dòng — để biết nó có bị đụng tới không.</summary>
    private async Task CreateLegacyDatabaseAsync(string dbName)
    {
        await using (var admin = await OpenAsync("postgres"))
        {
            await admin.ExecuteAsync($"CREATE DATABASE \"{dbName}\"");
        }

        await using var connection = await OpenAsync(dbName);
        await connection.ExecuteAsync("CREATE TABLE du_lieu_cu (ghi_chu text); INSERT INTO du_lieu_cu VALUES ('Dữ liệu Mẫu cũ')");
    }

    private async Task AssertLegacyUntouchedAsync(string dbName)
    {
        await using var connection = await OpenAsync(dbName);
        (await connection.QuerySingleAsync<string>("SELECT ghi_chu FROM du_lieu_cu")).ShouldBe("Dữ liệu Mẫu cũ");
        (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('public.app_user') IS NOT NULL")).ShouldBeFalse();
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private MasterStore Master() => new(fixture.MasterConnection);

    private TenantRegistry Tenants() => new(fixture.TenantTemplate, TimeProvider.System);

    [Fact]
    public async Task Web_tu_choi_ma_tro_toi_CSDL_master_va_khong_dung_vao_master()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        var masterName = new NpgsqlConnectionStringBuilder(fixture.MasterConnection).Database!; // lis_master_test
        var code = masterName["lis_".Length..].Replace('_', '-'); // → lis_master_test

        var response = await host.Anonymous.PostAsJsonAsync("/api/setup/clinic", Body(code), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonOf(response)).GetProperty("message").GetString()!.ShouldContain("CSDL");
        (await TableExistsAsync(masterName, "app_user")).ShouldBeFalse();
        (await Master().FindClinicAsync(code)).ShouldBeNull();
    }

    [Fact]
    public async Task Ma_master_khong_bien_lis_master_thanh_CSDL_phong_kham()
    {
        // Máy thật đặt master là lis_master: mã "master" cho ra đúng tên đó.
        var master = new MasterStore(ConnectionTo("lis_master"));
        await master.EnsureSchemaAsync();
        var provisioner = new ClinicProvisioner(master, Tenants());

        var ex = await Should.ThrowAsync<ApiException>(() => provisioner.CreateAsync("master", "Phòng khám Mẫu", "admin", "Quản trị Thử", Password));

        ex.Message.ShouldContain("CSDL");
        (await TableExistsAsync("lis_master", "app_user")).ShouldBeFalse();
        (await master.FindClinicAsync("master")).ShouldBeNull();

        await Should.ThrowAsync<ApiException>(() => provisioner.AddAsync("khac", "Phòng khám Mẫu", "lis_master", _ => { }));
        (await TableExistsAsync("lis_master", "app_user")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("template1")]
    [InlineData("TEMPLATE1")]
    public async Task Dong_lenh_tu_choi_CSDL_he_thong_cua_PostgreSQL(string dbName)
    {
        // template1 là khuôn của mọi CSDL tạo sau — migrate vào đó thì phòng khám nào tạo sau cũng mang sẵn bảng.
        var provisioner = new ClinicProvisioner(Master(), Tenants());

        var ex = await Should.ThrowAsync<ApiException>(() => provisioner.AddAsync("pk-he-thong", "Phòng khám Mẫu", dbName, _ => { }));

        ex.Status.ShouldBe(409);
        ex.Message.ShouldContain("CSDL hệ thống");
        (await TableExistsAsync("template1", "app_user")).ShouldBeFalse();
        (await TableExistsAsync("postgres", "app_user")).ShouldBeFalse();
        (await Master().FindClinicAsync("pk-he-thong")).ShouldBeNull();
    }

    [Fact]
    public async Task Tao_loi_giua_chung_roi_thu_lai_cung_ma_thi_duoc_ngay()
    {
        var provisioner = new ClinicProvisioner(Master(), Tenants());

        // Lỗi ở bước tạo tài khoản — sau khi đã mở kết nối tới CSDL mới — nên CSDL bị xoá (kéo theo kết nối trong pool).
        await Should.ThrowAsync<ApiException>(() => provisioner.CreateAsync("pk-thu-lai", "Phòng khám Mẫu", "ad\0min", "Quản trị Thử", Password));
        (await DatabaseExistsAsync("lis_pk_thu_lai")).ShouldBeFalse();

        // Thử lại cùng mã: phải dùng kết nối mới, không vấp kết nối cũ đã chết.
        var clinic = await provisioner.CreateAsync("pk-thu-lai", "Phòng khám Mẫu", "admin", "Quản trị Thử", Password);

        clinic.DbName.ShouldBe("lis_pk_thu_lai");
        (await TableExistsAsync("lis_pk_thu_lai", "app_user")).ShouldBeTrue();
    }

    [Fact]
    public async Task Web_tu_choi_409_khi_CSDL_da_ton_tai_va_giu_nguyen_no()
    {
        await using var host = await ApiHost.StartAsync(fixture, Key);
        await CreateLegacyDatabaseAsync("lis_pk_cu_web");

        var response = await host.Anonymous.PostAsJsonAsync("/api/setup/clinic", Body("pk-cu-web"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var message = (await JsonOf(response)).GetProperty("message").GetString()!;
        message.ShouldContain("đã tồn tại");
        message.ShouldContain("clinic add");
        (await Master().FindClinicAsync("pk-cu-web")).ShouldBeNull();
        await AssertLegacyUntouchedAsync("lis_pk_cu_web");
    }

    [Fact]
    public async Task Dong_lenh_van_dung_lai_CSDL_co_san()
    {
        await CreateLegacyDatabaseAsync("lis_pk_cu_cli");
        var master = Master();
        await master.EnsureSchemaAsync();

        (await AdminCommands.AddClinicAsync("pk-cu-cli", "Phòng khám Mẫu", "lis_pk_cu_cli", master, Tenants())).ShouldBe(0);

        (await master.FindClinicAsync("pk-cu-cli"))!.DbName.ShouldBe("lis_pk_cu_cli");
        await using var connection = await OpenAsync("lis_pk_cu_cli");
        (await connection.QuerySingleAsync<string>("SELECT ghi_chu FROM du_lieu_cu")).ShouldBe("Dữ liệu Mẫu cũ");
        (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('public.app_user') IS NOT NULL")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("lis_master_test")]
    [InlineData("LIS_MASTER_TEST")]
    [InlineData("1_bat_dau_bang_so")]
    [InlineData("co-gach-ngang")]
    [InlineData("co\"nhay")]
    [InlineData("co dau cách")]
    [InlineData("tiếng_việt")]
    [InlineData("")]
    [InlineData("a23456789012345678901234567890123456789012345678901234567890123x")] // 64 ký tự
    public async Task Dong_lenh_tu_choi_CSDL_master_va_ten_khong_hop_le(string dbName)
    {
        var master = Master();
        await master.EnsureSchemaAsync();

        var exit = await AdminCommands.AddClinicAsync("pk-ten-xau", "Phòng khám Mẫu", dbName, master, Tenants());

        exit.ShouldNotBe(0);
        (await master.FindClinicAsync("pk-ten-xau")).ShouldBeNull();
        (await TableExistsAsync(new NpgsqlConnectionStringBuilder(fixture.MasterConnection).Database!, "app_user")).ShouldBeFalse();
    }

    [Fact]
    public async Task Ten_CSDL_63_ky_tu_hop_le_van_tao_duoc()
    {
        var dbName = "lis_" + new string('a', 59);
        dbName.Length.ShouldBe(63);
        var master = Master();
        await master.EnsureSchemaAsync();

        (await AdminCommands.AddClinicAsync("pk-63", "Phòng khám Mẫu", dbName, master, Tenants())).ShouldBe(0);
        (await DatabaseExistsAsync(dbName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Dong_lenh_trung_ma_phong_kham_in_thong_bao_va_tra_ma_loi_khong_nem_ngoai_le()
    {
        var master = Master();
        await master.EnsureSchemaAsync();
        (await AdminCommands.AddClinicAsync("pk-trung", "Phòng khám Mẫu", "lis_pk_trung", master, Tenants())).ShouldBe(0);

        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        int exit;
        try
        {
            exit = await AdminCommands.AddClinicAsync("pk-trung", "Phòng khám Mẫu 2", "lis_pk_trung_2", master, Tenants());
        }
        finally
        {
            Console.SetError(original);
        }

        exit.ShouldNotBe(0);
        captured.ToString().ShouldContain("Mã phòng khám đã tồn tại");
        (await DatabaseExistsAsync("lis_pk_trung_2")).ShouldBeFalse();
    }
}
