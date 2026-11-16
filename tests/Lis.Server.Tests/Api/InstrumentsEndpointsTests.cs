using Shouldly;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Core.Instruments;
using Lis.Server.Tenancy;
using Npgsql;

namespace Lis.Server.Tests.Api;

public sealed class InstrumentsEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string AdminOnly = "Chỉ tài khoản Quản trị được thay đổi cài đặt này";
    private const string BadUuid = "Validation failed (uuid is expected)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldBe(message);
    }

    private sealed record Setup(ApiHost Host, Clinic Clinic, HttpClient Admin, HttpClient Tech, string Connection) : IAsyncDisposable
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

        public Task<Guid> Service(string code, string name, bool panel = false) =>
            Scalar<Guid>(
                "INSERT INTO service (code, name, value_type, is_panel) VALUES (@code, @name, 'so', @panel) RETURNING id", new { code, name, panel });
    }

    private async Task<Setup> StartAsync(string code)
    {
        var host = await ApiHost.StartAsync(fixture);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        var connection = fixture.TenantTemplate.Replace("{db}", $"lis_{code}", StringComparison.Ordinal);
        return new Setup(host, clinic, await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password), connection);
    }

    private static Dictionary<string, object?> None() => new() { ["kind"] = "none" };

    private static Dictionary<string, object?> Body(
        string name = "Máy Thử", object? connection = null, Action<Dictionary<string, object?>>? edit = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name, ["model"] = null, ["protocolFamily"] = null, ["workstationCode"] = null, ["active"] = true,
            ["connection"] = connection ?? None(),
        };
        edit?.Invoke(body);
        return body;
    }

    private static object Channel(string channelCode = "GLU", string serviceCode = "GLU", int? decimals = null, decimal? factor = null,
        bool keep = false, bool send = false) =>
        new { channelCode, serviceCode, decimals, factor, keepComparator = keep, sendOnQuery = send };

    private static async Task<Guid> Create(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync("/api/instruments", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return Guid.Parse((await JsonOf(response)).GetProperty("id").GetString()!);
    }

    private static async Task<Guid> AddChannel(HttpClient client, Guid instrument, object channel)
    {
        var response = await client.PostAsJsonAsync($"/api/instruments/{instrument}/channels", channel, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return Guid.Parse((await JsonOf(response)).GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonOf(response);
    }

    private static async Task<JsonElement> One(HttpClient client, Guid id) =>
        (await Get(client, "/api/instruments")).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id.ToString());

    [Fact]
    public async Task Chua_dang_nhap_thi_401_o_moi_route()
    {
        await using var s = await StartAsync("pki401");
        var id = Guid.NewGuid();
        var anonymous = s.Host.Anonymous;

        (await anonymous.GetAsync("/api/instruments", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/instruments", Body(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync($"/api/instruments/{id}", Body(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/instruments/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/instruments/{id}/channels", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/instruments/{id}/channels", Channel(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync($"/api/instruments/{id}/channels/{id}", Channel(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/instruments/{id}/channels/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "GLU" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/instruments/{id}/declared/GLU", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/instruments/{id}/unmapped", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Khong_phai_quan_tri_xem_duoc_nhung_ghi_thi_403()
    {
        await using var s = await StartAsync("pki403");
        var id = await Create(s.Admin, Body());
        var service = await s.Service("GLU", "Glucose Mẫu");
        var channel = await AddChannel(s.Admin, id, Channel());

        (await s.Tech.GetAsync("/api/instruments", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Tech.GetAsync($"/api/instruments/{id}/channels", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Tech.GetAsync($"/api/instruments/{id}/unmapped", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/instruments", Body(), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PutAsJsonAsync($"/api/instruments/{id}", Body(), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.DeleteAsync($"/api/instruments/{id}", Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PostAsJsonAsync($"/api/instruments/{id}/channels", Channel("KHAC"), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PutAsJsonAsync($"/api/instruments/{id}/channels/{channel}", Channel(), Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.DeleteAsync($"/api/instruments/{id}/channels/{channel}", Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "GLU" }, Ct), HttpStatusCode.Forbidden, AdminOnly);
        await ShouldFail(await s.Tech.DeleteAsync($"/api/instruments/{id}/declared/GLU", Ct), HttpStatusCode.Forbidden, AdminOnly);
        // Kiểm đầu vào chạy trước kiểm quyền (pipe trước handler).
        await ShouldFail(await s.Tech.PostAsJsonAsync("/api/instruments", Body(""), Ct), HttpStatusCode.BadRequest, "Nhập tên máy");
        await ShouldFail(await s.Tech.DeleteAsync("/api/instruments/khong-phai-uuid", Ct), HttpStatusCode.BadRequest, BadUuid);
        service.ShouldNotBe(Guid.Empty);
    }

    public static TheoryData<string, Dictionary<string, object?>, ConnectionKind> ConnectionCases => new()
    {
        { "none", new() { ["kind"] = "none" }, ConnectionKind.None },
        {
            "serial",
            new() { ["kind"] = "serial", ["portName"] = " com3 ", ["baudRate"] = 19200, ["dataBits"] = 7, ["parity"] = "Even", ["stopBits"] = 2 },
            ConnectionKind.Serial
        },
        { "tcp-server", new() { ["kind"] = "tcp-server", ["tcpPort"] = 5100 }, ConnectionKind.TcpServer },
        { "tcp-client", new() { ["kind"] = "tcp-client", ["host"] = " 192.168.1.20 ", ["tcpPort"] = 50001 }, ConnectionKind.TcpClient },
    };

    [Theory]
    [MemberData(nameof(ConnectionCases))]
    public async Task Moi_kieu_ket_noi_luu_roi_doc_lai_dung_va_agent_doc_duoc(string kind, Dictionary<string, object?> connection, ConnectionKind expected)
    {
        await using var s = await StartAsync($"pkik{kind.Replace("-", "")}");
        var id = await Create(s.Admin, Body("Máy Thử", connection, b => { b["model"] = "  AU480  "; b["protocolFamily"] = "beckman-au"; }));

        var item = await One(s.Tech, id);

        item.GetProperty("name").GetString().ShouldBe("Máy Thử");
        item.GetProperty("model").GetString().ShouldBe("AU480");
        item.GetProperty("protocolFamily").GetString().ShouldBe("beckman-au");
        item.GetProperty("active").GetBoolean().ShouldBeTrue();
        item.GetProperty("channelCount").GetInt32().ShouldBe(0);
        item.GetProperty("pendingServiceCount").GetInt32().ShouldBe(0);
        item.GetProperty("unmappedCount").GetInt32().ShouldBe(0);
        item.GetProperty("agent").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
        var read = item.GetProperty("connection");
        read.GetProperty("kind").GetString().ShouldBe(kind);
        var text = item.GetProperty("connectionText");
        switch (kind)
        {
            case "serial":
                read.GetProperty("portName").GetString().ShouldBe("COM3");
                read.GetProperty("baudRate").GetInt32().ShouldBe(19200);
                read.GetProperty("dataBits").GetInt32().ShouldBe(7);
                read.GetProperty("parity").GetString().ShouldBe("Even");
                read.GetProperty("stopBits").GetInt32().ShouldBe(2);
                text.GetString().ShouldBe("COM3 · 19200 7E2");
                break;
            case "tcp-server":
                read.GetProperty("tcpPort").GetInt32().ShouldBe(5100);
                text.GetString().ShouldBe("TCP chờ máy nối vào cổng 5100");
                break;
            case "tcp-client":
                read.GetProperty("host").GetString().ShouldBe("192.168.1.20");
                read.GetProperty("tcpPort").GetInt32().ShouldBe(50001);
                text.GetString().ShouldBe("TCP nối tới 192.168.1.20:50001");
                break;
            default:
                text.ValueKind.ShouldBe(JsonValueKind.Null);
                break;
        }

        // Đường agent (GET /agent/v1/instruments) đọc cùng cột: bộ đọc cấu hình kết nối của agent phải nhận được.
        var stored = (await s.Host.TenantOf(s.Clinic).Instruments.ListAsync()).Single(i => i.Id == id);
        var settings = ConnectionSettings.Parse(stored.ConnectionJson);
        settings.Kind.ShouldBe(expected);
        settings.UnreadableJson.ShouldBeNull();
        settings.Validate().ShouldBeNull();
        switch (expected)
        {
            case ConnectionKind.Serial:
                (settings.PortName, settings.BaudRate, settings.DataBits, settings.Parity, settings.StopBits).ShouldBe(("COM3", 19200, 7, "Even", 2));
                break;
            case ConnectionKind.TcpServer:
                settings.TcpPort.ShouldBe(5100);
                break;
            case ConnectionKind.TcpClient:
                (settings.Host, settings.TcpPort).ShouldBe(("192.168.1.20", 50001));
                break;
            default:
                stored.ConnectionJson.ShouldBeNull();
                break;
        }
    }

    [Fact]
    public async Task Serial_thieu_tuy_chon_lay_mac_dinh_9600_8N1()
    {
        await using var s = await StartAsync("pkidef");
        var id = await Create(s.Admin, Body("Máy Thử", new { kind = "serial", portName = "COM1" }));

        var read = (await One(s.Admin, id)).GetProperty("connection");

        (read.GetProperty("baudRate").GetInt32(), read.GetProperty("dataBits").GetInt32(), read.GetProperty("parity").GetString(), read.GetProperty("stopBits").GetInt32())
            .ShouldBe((9600, 8, "None", 1));
        (await One(s.Admin, id)).GetProperty("connectionText").GetString().ShouldBe("COM1 · 9600 8N1");
    }

    [Fact]
    public async Task Giao_thuc_la_thi_400_con_chi_nghe_va_ba_ho_kia_duoc_nhan()
    {
        await using var s = await StartAsync("pkifam");

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/instruments", Body("Máy Thử", null, b => b["protocolFamily"] = "la-hoac"), Ct),
            HttpStatusCode.BadRequest, "Giao thức chưa được hỗ trợ");
        foreach (var family in new[] { "chi-nghe", "hl7-mllp", "astm-e1394", "beckman-au" })
        {
            var id = await Create(s.Admin, Body($"Máy {family} Thử", null, b => b["protocolFamily"] = family));
            (await One(s.Admin, id)).GetProperty("protocolFamily").GetString().ShouldBe(family);
        }

        var none = await Create(s.Admin, Body("Máy trống Thử", null, b => b["protocolFamily"] = null));
        (await One(s.Admin, none)).GetProperty("protocolFamily").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    public static TheoryData<string, Action<Dictionary<string, object?>>, string> InvalidBodies => new()
    {
        { "ten-trong", b => b["name"] = "", "Nhập tên máy" },
        { "ten-toan-trang", b => b["name"] = "   ", "Nhập tên máy" },
        { "thieu-ten", b => b.Remove("name"), "Dữ liệu gửi lên không hợp lệ" },
        { "thieu-active", b => b.Remove("active"), "Dữ liệu gửi lên không hợp lệ" },
        { "thieu-connection", b => b.Remove("connection"), "Dữ liệu gửi lên không hợp lệ" },
        { "kieu-sai", b => b["connection"] = new { kind = "usb" }, "Kiểu kết nối không hợp lệ" },
        { "baud-sai", b => b["connection"] = new { kind = "serial", portName = "COM1", baudRate = 1000 }, "Tốc độ baud không hợp lệ" },
        { "databits-sai", b => b["connection"] = new { kind = "serial", portName = "COM1", dataBits = 6 }, "Số bit dữ liệu phải là 7 hoặc 8" },
        { "parity-sai", b => b["connection"] = new { kind = "serial", portName = "COM1", parity = "Mark" }, "Parity không hợp lệ" },
        { "stopbits-sai", b => b["connection"] = new { kind = "serial", portName = "COM1", stopBits = 3 }, "Stop bit phải là 1 hoặc 2" },
        { "port-qua-lon", b => b["connection"] = new { kind = "tcp-server", tcpPort = 70000 }, "Cổng TCP phải từ 1 đến 65535" },
        { "port-bang-0", b => b["connection"] = new { kind = "tcp-server", tcpPort = 0 }, "Cổng TCP phải từ 1 đến 65535" },
        { "serial-thieu-cong", b => b["connection"] = new { kind = "serial" }, "Chọn cổng COM" },
        { "tcp-thieu-cong", b => b["connection"] = new { kind = "tcp-server" }, "Cổng TCP phải từ 1 đến 65535" },
        { "client-thieu-host", b => b["connection"] = new { kind = "tcp-client", tcpPort = 5100 }, "Nhập địa chỉ IP của máy" },
        { "agent-la", b => b["workstationCode"] = "XN-99", "Không có agent nào dùng mã máy trạm XN-99" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Thieu_hoac_sai_du_lieu_thi_400_dung_cau(string name, Action<Dictionary<string, object?>> edit, string message)
    {
        await using var s = await StartAsync("pkibad" + name.Replace("-", "_"));

        await ShouldFail(await s.Admin.PostAsJsonAsync("/api/instruments", Body("Máy Thử", null, edit), Ct), HttpStatusCode.BadRequest, message);
        (await Get(s.Admin, "/api/instruments")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Truong_la_thi_400_va_uuid_sai_thi_400()
    {
        await using var s = await StartAsync("pkiextra");
        var id = await Create(s.Admin, Body());

        (await s.Admin.PostAsJsonAsync("/api/instruments", Body("Máy Thử", null, b => b["la"] = 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await ShouldFail(await s.Admin.PutAsJsonAsync("/api/instruments/xyz", Body(), Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.GetAsync("/api/instruments/xyz/channels", Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/xyz", Channel(), Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.DeleteAsync($"/api/instruments/{id}/channels/xyz", Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Admin.GetAsync("/api/instruments/xyz/unmapped", Ct), HttpStatusCode.BadRequest, BadUuid);
    }

    [Fact]
    public async Task Gan_may_cho_agent_khong_phan_biet_hoa_thuong_va_dong_online_kem_trang_thai()
    {
        await using var s = await StartAsync("pkiagent");
        var agent = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm Thử", "XN-01");
        var other = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm khác Thử", "XN-02");
        var id = await Create(s.Admin, Body("Máy Thử", null, b => b["workstationCode"] = "  xn-01 "));
        var idle = await Create(s.Admin, Body("Máy rảnh Thử", null, b => b["workstationCode"] = "XN-02"));
        await s.Host.TouchAgentAsync(agent, $$"""[{"instrumentId":"{{id}}","state":"Connected","text":"Đã nối","lastResultAt":null,"samplesReceived":3,"pendingToRetry":0}]""");

        var item = await One(s.Admin, id);

        item.GetProperty("workstationCode").GetString().ShouldBe("xn-01");
        item.GetProperty("agent").GetProperty("id").GetString().ShouldBe(agent.Id.ToString());
        item.GetProperty("agent").GetProperty("name").GetString().ShouldBe("Máy trạm Thử");
        item.GetProperty("agent").GetProperty("online").GetBoolean().ShouldBeTrue();
        item.GetProperty("agent").GetProperty("lastSeenAt").GetString().ShouldEndWith("Z");
        var status = item.GetProperty("status");
        status.GetProperty("state").GetString().ShouldBe("Connected");
        status.GetProperty("samplesReceived").GetInt32().ShouldBe(3);
        // Agent khác chưa báo gì: có agent nhưng chưa online, không có trạng thái.
        var idleItem = await One(s.Admin, idle);
        idleItem.GetProperty("agent").GetProperty("id").GetString().ShouldBe(other.Id.ToString());
        idleItem.GetProperty("agent").GetProperty("online").GetBoolean().ShouldBeFalse();
        idleItem.GetProperty("agent").GetProperty("lastSeenAt").ValueKind.ShouldBe(JsonValueKind.Null);
        idleItem.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Agent_im_hon_90_giay_thi_offline_va_khong_hien_trang_thai()
    {
        await using var s = await StartAsync("pkistale");
        var agent = await s.Host.AddAgentAsync(s.Clinic, "Máy trạm Thử", "XN-01");
        var id = await Create(s.Admin, Body("Máy Thử", null, b => b["workstationCode"] = "XN-01"));
        await s.Host.TouchAgentAsync(agent, $$"""[{"instrumentId":"{{id}}","state":"Connected","text":"Đã nối","lastResultAt":null,"samplesReceived":0,"pendingToRetry":0}]""");
        await using (var master = new NpgsqlConnection(fixture.MasterConnection))
        {
            await master.ExecuteAsync("UPDATE agent SET last_seen_at = now() - interval '5 minutes' WHERE id = @id", new { id = agent.Id });
        }

        var item = await One(s.Admin, id);

        item.GetProperty("agent").GetProperty("online").GetBoolean().ShouldBeFalse();
        item.GetProperty("agent").GetProperty("lastSeenAt").GetString().ShouldNotBeNull();
        item.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Sua_may_ghi_de_thong_tin_nhung_giu_driver_config_va_khong_co_may_thi_404()
    {
        await using var s = await StartAsync("pkiput");
        var id = await Create(s.Admin, Body("Máy cũ Thử", new { kind = "tcp-server", tcpPort = 5100 }));
        await s.Exec("UPDATE instrument SET driver_config = '{\"classB\":true}'::jsonb WHERE id = @id", new { id });

        var response = await s.Admin.PutAsJsonAsync($"/api/instruments/{id}", Body("Máy mới Thử", new { kind = "tcp-client", host = "10.0.0.5", tcpPort = 6000 },
            b => { b["active"] = false; b["model"] = "Mẫu X"; }), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var item = await One(s.Admin, id);
        item.GetProperty("name").GetString().ShouldBe("Máy mới Thử");
        item.GetProperty("active").GetBoolean().ShouldBeFalse();
        item.GetProperty("model").GetString().ShouldBe("Mẫu X");
        item.GetProperty("connection").GetProperty("kind").GetString().ShouldBe("tcp-client");
        (await s.Scalar<string>("SELECT driver_config::text FROM instrument WHERE id = @id", new { id })).ShouldContain("classB");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{Guid.NewGuid()}", Body(), Ct), HttpStatusCode.NotFound, "Không tìm thấy máy");
    }

    [Fact]
    public async Task Xoa_may_xoa_ca_kenh_va_khai_bao_va_may_da_co_ket_qua_thi_409()
    {
        await using var s = await StartAsync("pkidel");
        await s.Service("GLU", "Glucose Mẫu");
        await s.Service("URE", "Urea Mẫu");
        var free = await Create(s.Admin, Body("Máy trống Thử"));
        await AddChannel(s.Admin, free, Channel());
        (await s.Admin.PostAsJsonAsync($"/api/instruments/{free}/declared", new { serviceCode = "URE" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var used = await Create(s.Admin, Body("Máy dùng Thử"));
        await s.Exec("INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, reason) VALUES (@used, 'B1', 'X', '1', 'kenh-chua-map')", new { used });

        (await s.Admin.DeleteAsync($"/api/instruments/{free}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldFail(await s.Admin.DeleteAsync($"/api/instruments/{used}", Ct), HttpStatusCode.Conflict,
            "Máy đã có kết quả xét nghiệm gắn với nó nên không xoá được — hãy chuyển sang \"Ngừng dùng\"");
        await ShouldFail(await s.Admin.DeleteAsync($"/api/instruments/{Guid.NewGuid()}", Ct), HttpStatusCode.NotFound, "Không tìm thấy máy");

        (await s.Scalar<int>("SELECT count(*)::int FROM instrument_channel WHERE instrument_id = @free", new { free })).ShouldBe(0);
        (await s.Scalar<int>("SELECT count(*)::int FROM instrument_service WHERE instrument_id = @free", new { free })).ShouldBe(0);
        (await s.Scalar<int>("SELECT count(*)::int FROM instrument WHERE id = @used", new { used })).ShouldBe(1);
    }

    [Fact]
    public async Task Bang_map_kenh_gom_kenh_da_map_roi_chi_so_khai_bao_cho_do_ma_kenh()
    {
        await using var s = await StartAsync("pkichan");
        await s.Service("GLU", "Glucose Mẫu");
        await s.Service("URE", "Urea Mẫu");
        await s.Service("CRE", "Creatinin Mẫu");
        var id = await Create(s.Admin, Body());
        var other = await Create(s.Admin, Body("Máy khác Thử"));
        await AddChannel(s.Admin, id, Channel("ZZ", "gLu", 2, 0.5m, keep: true, send: true));
        await AddChannel(s.Admin, id, Channel("AA", "URE"));
        await AddChannel(s.Admin, other, Channel("QQ", "CRE"));
        (await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "cre" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // Khai lại một chỉ số đã khai và một chỉ số đã map kênh: không lỗi, không lặp.
        (await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "CRE" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "URE" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var rows = (await Get(s.Tech, $"/api/instruments/{id}/channels")).EnumerateArray().ToList();

        rows.Select(r => r.GetProperty("serviceCode").GetString()).ShouldBe(["URE", "GLU", "CRE"]);
        rows[0].GetProperty("channelCode").GetString().ShouldBe("AA");
        rows[0].GetProperty("id").GetString().ShouldNotBeNull();
        rows[0].GetProperty("serviceName").GetString().ShouldBe("Urea Mẫu");
        rows[1].GetProperty("decimals").GetInt32().ShouldBe(2);
        rows[1].GetProperty("factor").GetDecimal().ShouldBe(0.5m);
        rows[1].GetProperty("keepComparator").GetBoolean().ShouldBeTrue();
        rows[1].GetProperty("sendOnQuery").GetBoolean().ShouldBeTrue();
        // Chỉ số khai báo chưa biết mã kênh: id và channelCode null, gửi khi truy vấn mặc định bật.
        rows[2].GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[2].GetProperty("channelCode").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[2].GetProperty("decimals").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[2].GetProperty("factor").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[2].GetProperty("keepComparator").GetBoolean().ShouldBeFalse();
        rows[2].GetProperty("sendOnQuery").GetBoolean().ShouldBeTrue();
        var item = await One(s.Tech, id);
        (item.GetProperty("channelCount").GetInt32(), item.GetProperty("pendingServiceCount").GetInt32()).ShouldBe((2, 1));
        await ShouldFail(await s.Tech.GetAsync($"/api/instruments/{Guid.NewGuid()}/channels", Ct), HttpStatusCode.NotFound, "Không tìm thấy máy");
    }

    [Fact]
    public async Task Bo_khai_bao_theo_ma_khong_phan_biet_hoa_thuong_va_ma_la_thi_bo_qua()
    {
        await using var s = await StartAsync("pkiundecl");
        await s.Service("CRE", "Creatinin Mẫu");
        var id = await Create(s.Admin, Body());
        (await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "CRE" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await s.Admin.DeleteAsync($"/api/instruments/{id}/declared/khong-co", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Get(s.Admin, $"/api/instruments/{id}/channels")).GetArrayLength().ShouldBe(1);
        (await s.Admin.DeleteAsync($"/api/instruments/{id}/declared/cre", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Get(s.Admin, $"/api/instruments/{id}/channels")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Khai_bao_chi_so_la_goi_hay_ma_sai_hay_may_khong_co_thi_loi()
    {
        await using var s = await StartAsync("pkidecl");
        await s.Service("GOI", "Gói Mẫu", panel: true);
        var id = await Create(s.Admin, Body());

        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "" }, Ct), HttpStatusCode.BadRequest, "Nhập mã chỉ số");
        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "KHONGCO" }, Ct), HttpStatusCode.BadRequest, "Không có chỉ số mã KHONGCO");
        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/instruments/{id}/declared", new { serviceCode = "GOI" }, Ct), HttpStatusCode.BadRequest,
            "Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (gói)");
        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/instruments/{Guid.NewGuid()}/declared", new { serviceCode = "GOI" }, Ct), HttpStatusCode.NotFound, "Không tìm thấy máy");
    }

    [Fact]
    public async Task Them_kenh_kiem_dau_vao_roi_chi_so_roi_trung_ma()
    {
        await using var s = await StartAsync("pkiadd");
        await s.Service("GLU", "Glucose Mẫu");
        await s.Service("URE", "Urea Mẫu");
        await s.Service("GOI", "Gói Mẫu", panel: true);
        var id = await Create(s.Admin, Body());
        string Url() => $"/api/instruments/{id}/channels";

        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel(""), Ct), HttpStatusCode.BadRequest, "Nhập mã kênh máy");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", ""), Ct), HttpStatusCode.BadRequest, "Nhập mã chỉ số");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", "KHONGCO"), Ct), HttpStatusCode.BadRequest, "Không có chỉ số mã KHONGCO");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", "GOI"), Ct), HttpStatusCode.BadRequest,
            "Kênh máy chỉ map tới chỉ số lá, không map tới dịch vụ cha (gói)");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", "GLU", factor: 0m), Ct), HttpStatusCode.BadRequest, "Hệ số phải lớn hơn 0");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", "GLU", factor: -1m), Ct), HttpStatusCode.BadRequest, "Hệ số phải lớn hơn 0");
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("A", "GLU", decimals: 7), Ct), HttpStatusCode.BadRequest, "Dữ liệu gửi lên không hợp lệ");
        await ShouldFail(await s.Admin.PostAsJsonAsync($"/api/instruments/{Guid.NewGuid()}/channels", Channel(), Ct), HttpStatusCode.NotFound, "Không tìm thấy máy");

        await AddChannel(s.Admin, id, Channel("  HGB ", "GLU"));
        // Trùng mã (kể cả khác khoảng trắng): 409 nêu đúng mã và chỉ số đang giữ mã đó (câu của Lis.Data).
        await ShouldFail(await s.Admin.PostAsJsonAsync(Url(), Channel("HGB", "URE"), Ct), HttpStatusCode.Conflict, "Mã kênh HGB đã map cho chỉ số Glucose Mẫu");
        (await s.Scalar<string>("SELECT channel_code FROM instrument_channel WHERE instrument_id = @id", new { id })).ShouldBe("HGB");
    }

    [Fact]
    public async Task Sua_kenh_doi_ma_doi_chi_so_trung_ma_hay_kenh_cua_may_khac_thi_loi()
    {
        await using var s = await StartAsync("pkiupd");
        await s.Service("GLU", "Glucose Mẫu");
        await s.Service("URE", "Urea Mẫu");
        var id = await Create(s.Admin, Body());
        var other = await Create(s.Admin, Body("Máy khác Thử"));
        var glu = await AddChannel(s.Admin, id, Channel("G", "GLU"));
        await AddChannel(s.Admin, id, Channel("U", "URE"));
        var foreign = await AddChannel(s.Admin, other, Channel("F", "GLU"));

        var ok = await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{glu}", Channel(" G2 ", "URE", 3, 2.5m, keep: true, send: true), Ct);

        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = (await Get(s.Admin, $"/api/instruments/{id}/channels")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == glu.ToString());
        (row.GetProperty("channelCode").GetString(), row.GetProperty("serviceCode").GetString(), row.GetProperty("decimals").GetInt32()).ShouldBe(("G2", "URE", 3));
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{glu}", Channel("U", "GLU"), Ct), HttpStatusCode.Conflict, "Mã kênh U đã map cho chỉ số Urea Mẫu");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{glu}", Channel("G", "KHONGCO"), Ct), HttpStatusCode.BadRequest, "Không có chỉ số mã KHONGCO");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{glu}", Channel("G", "GLU", factor: 0m), Ct), HttpStatusCode.BadRequest, "Hệ số phải lớn hơn 0");
        // Kênh của máy khác hay kênh không có → 404, và không đụng tới kênh của máy khác.
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{foreign}", Channel("HACK", "URE"), Ct), HttpStatusCode.NotFound, "Không tìm thấy kênh");
        await ShouldFail(await s.Admin.PutAsJsonAsync($"/api/instruments/{id}/channels/{Guid.NewGuid()}", Channel(), Ct), HttpStatusCode.NotFound, "Không tìm thấy kênh");
        (await s.Scalar<string>("SELECT channel_code FROM instrument_channel WHERE id = @foreign", new { foreign })).ShouldBe("F");
    }

    [Fact]
    public async Task Xoa_kenh_chi_xoa_kenh_cua_dung_may()
    {
        await using var s = await StartAsync("pkirmch");
        await s.Service("GLU", "Glucose Mẫu");
        var id = await Create(s.Admin, Body());
        var other = await Create(s.Admin, Body("Máy khác Thử"));
        var mine = await AddChannel(s.Admin, id, Channel("G", "GLU"));
        var foreign = await AddChannel(s.Admin, other, Channel("F", "GLU"));

        (await s.Admin.DeleteAsync($"/api/instruments/{id}/channels/{foreign}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Scalar<int>("SELECT count(*)::int FROM instrument_channel WHERE id = @foreign", new { foreign })).ShouldBe(1);
        (await s.Admin.DeleteAsync($"/api/instruments/{id}/channels/{mine}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Get(s.Admin, $"/api/instruments/{id}/channels")).GetArrayLength().ShouldBe(0);
        (await s.Admin.DeleteAsync($"/api/instruments/{id}/channels/{mine}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Kenh_chua_map_nhom_theo_ma_moi_nhat_truoc_va_dem_vao_danh_sach_may()
    {
        await using var s = await StartAsync("pkiunm");
        await s.Service("GLU", "Glucose Mẫu");
        var id = await Create(s.Admin, Body());
        await AddChannel(s.Admin, id, Channel("DA_MAP", "GLU"));
        async Task Unmatched(string channel, string value, string? unit, string age, string reason = "kenh-chua-map", bool resolved = false) =>
            await s.Exec(
                $"INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, unit, reason, received_at, resolved_at) VALUES (@id, 'B', @channel, @value, @unit, @reason, now() - interval '{age}', {(resolved ? "now()" : "NULL")})",
                new { id, channel, value, unit, reason });
        await Unmatched("WBC", "5.1", "G/L", "3 hours");
        await Unmatched("WBC", "7.7", "G/L", "1 hours");
        await Unmatched("RBC", "4.2", null, "2 hours");
        await Unmatched("DA_MAP", "9", null, "1 minutes");
        await Unmatched("DA_XONG", "9", null, "1 minutes", resolved: true);
        await Unmatched("KHAC_LY_DO", "9", null, "1 minutes", reason: "khong-co-barcode");

        var rows = (await Get(s.Tech, $"/api/instruments/{id}/unmapped")).EnumerateArray().ToList();

        rows.Select(r => r.GetProperty("channelCode").GetString()).ShouldBe(["WBC", "RBC"]);
        rows[0].GetProperty("count").GetInt32().ShouldBe(2);
        rows[0].GetProperty("lastValue").GetString().ShouldBe("7.7");
        rows[0].GetProperty("lastUnit").GetString().ShouldBe("G/L");
        rows[0].GetProperty("lastAt").GetString().ShouldEndWith("Z");
        rows[1].GetProperty("lastUnit").ValueKind.ShouldBe(JsonValueKind.Null);
        (await One(s.Tech, id)).GetProperty("unmappedCount").GetInt32().ShouldBe(2);
        (await Get(s.Tech, $"/api/instruments/{Guid.NewGuid()}/unmapped")).GetArrayLength().ShouldBe(0);
    }
}
