using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Core.Ingest;
using Lis.Core.Results;
using Lis.Data;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Lis.Data.Instruments;
using Lis.Data.Orders;
using Lis.Server.Tenancy;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.Api;

/// <summary>
/// <c>/api/samples</c> và <c>/api/results</c>: dạng JSON theo <c>SampleDetail</c> / <c>ResultHistory</c> của web, quyền theo vai trò,
/// mọi thao tác ghi đi qua <c>ResultService</c> với người thao tác là tài khoản của phiên, lọc ngày theo giờ phòng khám, phòng
/// khám khác không thấy được, và hai thao tác đồng thời trên cùng một mẫu không để lại trạng thái lệch.
/// </summary>
public sealed class SamplesEndpointsTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string Password = "mat-khau-thu-1";
    private const string BadUuid = "Validation failed (uuid is expected)";
    private const string InvalidBody = "Dữ liệu gửi lên không hợp lệ";
    private const string EditorOnly = "Tài khoản không có quyền nhập / duyệt kết quả";
    private const string UnapproverOnly = "Chỉ Trưởng khoa hoặc Quản trị được huỷ duyệt";
    private const string ApprovedLocked = "Mẫu đã duyệt — hủy duyệt trước khi sửa";
    private const string BadRange = "Khoảng ngày không hợp lệ (from, to dạng yyyy-MM-dd)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        var body = await JsonOf(response);
        body.GetProperty("statusCode").GetInt32().ShouldBe((int)status);
        body.GetProperty("message").GetString().ShouldBe(message);
    }

    private static string[] Props(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static async Task<JsonElement> Get(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await JsonOf(response);
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, Guid orderItemId, string? value, string? reason = null) =>
        client.PutAsJsonAsync($"/api/results/items/{orderItemId}", new { value, reason }, Ct);

    private static async Task Set(HttpClient client, Guid orderItemId, string value, string? reason = null) =>
        (await Put(client, orderItemId, value, reason)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static Task<HttpResponseMessage> Approve(HttpClient client, Guid sampleId) =>
        client.PostAsync($"/api/samples/{sampleId}/approve", null, Ct);

    private static Task<HttpResponseMessage> Unapprove(HttpClient client, Guid sampleId, string? reason) =>
        client.PostAsJsonAsync($"/api/samples/{sampleId}/unapprove", new { reason }, Ct);

    /// <summary>Một phòng khám thử: tài khoản bốn vai trò, cơ sở, loại mẫu EDTA. Tên đầy đủ của tài khoản là "{username} Thử".</summary>
    private sealed record Setup(
        ApiHost Host, Clinic Clinic, TenantServices Tenant, Guid EdtaId,
        HttpClient Admin, HttpClient Tech, HttpClient Head, HttpClient Reception,
        Guid AdminId, Guid TechId, Guid HeadId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public Db Db => Tenant.Db;

        public async Task<T> Scalar<T>(string sql, object? args = null)
        {
            await using var connection = await Db.OpenAsync();
            return (await connection.ExecuteScalarAsync<T>(sql, args))!;
        }

        public async Task<List<T>> Query<T>(string sql, object? args = null)
        {
            await using var connection = await Db.OpenAsync();
            return (await connection.QueryAsync<T>(sql, args)).ToList();
        }

        public async Task Exec(string sql, object? args = null)
        {
            await using var connection = await Db.OpenAsync();
            await connection.ExecuteAsync(sql, args);
        }

        public Task<Guid> Service(
            string code, string name, int? decimals = null, string? unit = null, string valueType = "so", bool panel = false,
            int printOrder = 0, bool autoApprove = false) =>
            Tenant.Catalog.SaveServiceAsync(new ServiceInfo(
                Guid.Empty, code, name, null, EdtaId, unit, valueType, decimals, printOrder, panel, autoApprove, true, null, []));

        /// <summary>Chỉ định cho một người bệnh giả → mẫu (duy nhất, EDTA) và dòng chỉ định theo dịch vụ.</summary>
        public async Task<(Guid SampleId, string Barcode, Dictionary<Guid, Guid> Items)> Order(
            NewPatient patient, Guid[] serviceIds, string? doctor = null, string? dept = null, string? diagnosis = null)
        {
            var created = await new OrderService(Db, TimeProvider.System).CreateAsync(
                new NewOrder(null, patient, serviceIds, doctor, dept, diagnosis), AdminId);
            var sample = created.Samples.Single();
            var items = await Query<(Guid ServiceId, Guid Id)>(
                "SELECT service_id, id FROM order_item WHERE sample_id = @sampleId", new { sampleId = sample.SampleId });
            return (sample.SampleId, sample.Barcode, items.ToDictionary(i => i.ServiceId, i => i.Id));
        }

        public async Task<StoredResult> Stored(Guid orderItemId)
        {
            await using var connection = await Db.OpenAsync();
            return await connection.QuerySingleAsync<StoredResult>(
                """
                SELECT id, value, ref_low, ref_high, flag, source, status, pending_value, updated_by
                FROM result WHERE order_item_id = @orderItemId
                """,
                new { orderItemId });
        }

        public Task<List<HistoryRow>> History(Guid orderItemId) =>
            Query<HistoryRow>(
                """
                SELECT h.action, h.old_value, h.new_value, h.reason, h.actor_id, h.at
                FROM result_history h JOIN result r ON r.id = h.result_id
                WHERE r.order_item_id = @orderItemId ORDER BY h.at, h.id
                """,
                new { orderItemId });

        public async Task<SampleRow> Sample(Guid sampleId) =>
            (await Query<SampleRow>("SELECT approved_at, approved_by FROM sample WHERE id = @sampleId", new { sampleId })).Single();
    }

    private sealed record StoredResult
    {
        public Guid Id { get; init; }
        public string? Value { get; init; }
        public decimal? RefLow { get; init; }
        public decimal? RefHigh { get; init; }
        public string? Flag { get; init; }
        public string? Source { get; init; }
        public string Status { get; init; } = "";
        public string? PendingValue { get; init; }
        public Guid? UpdatedBy { get; init; }
    }

    private sealed record HistoryRow
    {
        public string Action { get; init; } = "";
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? Reason { get; init; }
        public Guid? ActorId { get; init; }
        public DateTimeOffset At { get; init; }
    }

    private sealed record SampleRow
    {
        public DateTimeOffset? ApprovedAt { get; init; }
        public Guid? ApprovedBy { get; init; }
    }

    private static NewPatient Woman(string name = "Trần Thị Mẫu") => new(name, new DateOnly(1990, 3, 15), null, 'F', null, "0900000011");

    private static NewPatient Man(string name = "Nguyễn Văn Mẫu") => new(name, new DateOnly(1988, 7, 1), null, 'M', null, "0900000012");

    private async Task<Setup> StartAsync(string code, IReadOnlyDictionary<string, string>? settings = null)
    {
        var host = await ApiHost.StartAsync(fixture, settings: settings);
        var clinic = await host.AddClinicAsync(code, "Phòng khám Thử");
        await host.AddUserAsync(clinic, "admin", Password, "quan-tri");
        await host.AddUserAsync(clinic, "ktv", Password, "ky-thuat-vien");
        await host.AddUserAsync(clinic, "tk", Password, "truong-khoa");
        await host.AddUserAsync(clinic, "tn", Password, "tiep-nhan");
        var tenant = host.TenantOf(clinic);
        await new FacilityStore(tenant.Db).SaveAsync(new FacilityInfo("Phòng khám Thử", null, null, null, "9", "yyMMdd", 4, "per-specimen"));
        var edta = (await tenant.Catalog.SpecimenTypesAsync()).First(s => s.Code == "EDTA").Id;
        var users = await tenant.Users.ListAsync();
        Guid IdOf(string username) => users.Single(u => u.Username == username).Id;
        return new Setup(
            host, clinic, tenant, edta,
            await host.LoginAsync(code, "admin", Password), await host.LoginAsync(code, "ktv", Password),
            await host.LoginAsync(code, "tk", Password), await host.LoginAsync(code, "tn", Password),
            IdOf("admin"), IdOf("ktv"), IdOf("tk"));
    }

    private static string Today => DateTime.UtcNow.AddHours(7).ToString("yyyy-MM-dd");

    private static string ListUrl(string? extra = null)
    {
        // Rộng hai ngày quanh hôm nay theo giờ Việt Nam — chỉ định vừa tạo luôn nằm trong.
        var today = DateOnly.Parse(Today);
        return $"/api/samples?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}{extra}";
    }

    [Fact]
    public async Task Chua_dang_nhap_thi_401_o_moi_route()
    {
        await using var s = await StartAsync("pkkq401");
        var id = Guid.NewGuid();
        var anonymous = s.Host.Anonymous;

        (await anonymous.GetAsync(ListUrl(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/samples/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Approve(anonymous, id)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Unapprove(anonymous, id, "Thử")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Put(anonymous, id, "1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/results/items/{id}/history", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/results/{id}/pending/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/results/{id}/pending/reject", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Danh_sach_va_chi_tiet_dung_dang_SampleListItem_SampleDetail()
    {
        await using var s = await StartAsync("pkkqdang");
        var glu = await s.Service("GLU", "Glucose Mẫu", decimals: 1, unit: "mmol/L", printOrder: 1);
        await s.Tenant.Catalog.SaveRangesAsync(glu, [new ReferenceRange(Guid.Empty, glu, null, null, null, null, 3.9m, 6.4m, null, null, null)]);
        var panel = await s.Service("TPTTBM", "Tổng phân tích Mẫu", panel: true, printOrder: 2);
        var wbc = await s.Service("WBC", "Bạch cầu Mẫu", decimals: 2, unit: "G/L");
        var hbsag = await s.Service("HBSAG", "HBsAg Mẫu", valueType: "dinh-tinh");
        await s.Tenant.Catalog.SetChildrenAsync(panel, [wbc, hbsag]);
        await s.Tenant.Catalog.SaveQualitativeRulesAsync(hbsag,
        [
            new QualitativeRule(null, hbsag, "neg", "Âm tính", null),
            new QualitativeRule(null, hbsag, "pos", "Dương tính", ResultFlag.Abnormal),
            new QualitativeRule(null, hbsag, "NEG", "Âm tính", null),
        ]);
        var note = await s.Service("GHICHU", "Ghi chú Mẫu", valueType: "chu", printOrder: 3);

        var (sampleId, barcode, items) = await s.Order(Woman(), [glu, panel, note], "BS Thử", "Khoa Thử", "Chẩn đoán Thử");
        await Set(s.Tech, items[glu], "7,25");
        await Set(s.Tech, items[hbsag], "pos");

        var list = (await Get(s.Reception, ListUrl())).EnumerateArray().ToList();
        var listItem = list.Single();
        Props(listItem).ShouldBe(
            ["sampleId", "barcode", "patientName", "patientCode", "orderedAt", "specimenName", "total", "withValue", "status", "hasAbnormal", "hasPending"]);
        listItem.GetProperty("sampleId").GetString().ShouldBe(sampleId.ToString());
        listItem.GetProperty("barcode").GetString().ShouldBe(barcode);
        listItem.GetProperty("patientName").GetString().ShouldBe("Trần Thị Mẫu");
        listItem.GetProperty("patientCode").GetString()!.ShouldStartWith("BN");
        listItem.GetProperty("orderedAt").GetString()!.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        listItem.GetProperty("specimenName").GetString().ShouldNotBeNullOrEmpty();
        listItem.GetProperty("total").GetInt32().ShouldBe(4);
        listItem.GetProperty("withValue").GetInt32().ShouldBe(2);
        listItem.GetProperty("status").GetString().ShouldBe("cho-ket-qua");
        listItem.GetProperty("hasAbnormal").GetBoolean().ShouldBeTrue();
        listItem.GetProperty("hasPending").GetBoolean().ShouldBeFalse();

        var detail = await Get(s.Reception, $"/api/samples/{sampleId}");
        Props(detail).ShouldBe(["sampleId", "barcode", "patient", "order", "approvedAt", "approvedBy", "rows"]);
        detail.GetProperty("barcode").GetString().ShouldBe(barcode);
        var patient = detail.GetProperty("patient");
        Props(patient).ShouldBe(["name", "code", "sex", "dob", "birthYear"]);
        patient.GetProperty("name").GetString().ShouldBe("Trần Thị Mẫu");
        patient.GetProperty("code").GetString().ShouldBe(listItem.GetProperty("patientCode").GetString());
        patient.GetProperty("sex").GetString().ShouldBe("F");
        patient.GetProperty("dob").GetString().ShouldBe("1990-03-15");
        patient.GetProperty("birthYear").ValueKind.ShouldBe(JsonValueKind.Null);
        var order = detail.GetProperty("order");
        Props(order).ShouldBe(["doctor", "dept", "diagnosis", "orderedAt"]);
        order.GetProperty("doctor").GetString().ShouldBe("BS Thử");
        order.GetProperty("dept").GetString().ShouldBe("Khoa Thử");
        order.GetProperty("diagnosis").GetString().ShouldBe("Chẩn đoán Thử");
        order.GetProperty("orderedAt").GetString()!.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        detail.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("approvedBy").ValueKind.ShouldBe(JsonValueKind.Null);

        var rows = detail.GetProperty("rows").EnumerateArray().ToList();
        Props(rows[0]).ShouldBe(
        [
            "orderItemId", "resultId", "valueType", "choices", "serviceCode", "serviceName", "isPanelHeader", "depth", "value", "unit",
            "refText", "flag", "status", "pendingValue", "source",
        ]);
        rows.Select(r => (r.GetProperty("serviceCode").GetString(), r.GetProperty("depth").GetInt32(), r.GetProperty("isPanelHeader").GetBoolean()))
            .ShouldBe([("GLU", 0, false), ("TPTTBM", 0, true), ("WBC", 1, false), ("HBSAG", 1, false), ("GHICHU", 0, false)]);

        var gluRow = rows[0];
        gluRow.GetProperty("orderItemId").GetString().ShouldBe(items[glu].ToString());
        gluRow.GetProperty("resultId").GetString().ShouldBe((await s.Stored(items[glu])).Id.ToString());
        gluRow.GetProperty("valueType").GetString().ShouldBe("so");
        gluRow.GetProperty("choices").GetArrayLength().ShouldBe(0);
        gluRow.GetProperty("serviceName").GetString().ShouldBe("Glucose Mẫu");
        gluRow.GetProperty("value").GetString().ShouldBe("7.3");
        gluRow.GetProperty("unit").GetString().ShouldBe("mmol/L");
        gluRow.GetProperty("refText").GetString().ShouldBe("3.9 – 6.4");
        gluRow.GetProperty("flag").GetString().ShouldBe("High");
        gluRow.GetProperty("status").GetString().ShouldBe("cho-duyet");
        gluRow.GetProperty("pendingValue").ValueKind.ShouldBe(JsonValueKind.Null);
        gluRow.GetProperty("source").GetString().ShouldBe("tay");

        var panelRow = rows[1];
        panelRow.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        panelRow.GetProperty("unit").ValueKind.ShouldBe(JsonValueKind.Null);
        panelRow.GetProperty("refText").ValueKind.ShouldBe(JsonValueKind.Null);
        panelRow.GetProperty("resultId").ValueKind.ShouldBe(JsonValueKind.Null);

        var wbcRow = rows[2];
        wbcRow.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        wbcRow.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
        wbcRow.GetProperty("flag").GetString().ShouldBe("None");
        wbcRow.GetProperty("unit").GetString().ShouldBe("G/L");

        var hbsagRow = rows[3];
        hbsagRow.GetProperty("valueType").GetString().ShouldBe("dinh-tinh");
        // Không trùng; thứ tự theo collation của CSDL (cùng câu ORDER BY display_value như NestJS), không theo thứ tự ký tự.
        hbsagRow.GetProperty("choices").EnumerateArray().Select(c => c.GetString()).ShouldBe(["Âm tính", "Dương tính"], ignoreOrder: true);
        hbsagRow.GetProperty("value").GetString().ShouldBe("Dương tính");
        hbsagRow.GetProperty("flag").GetString().ShouldBe("Abnormal");

        rows[4].GetProperty("valueType").GetString().ShouldBe("chu");

        // Đủ giá trị → chờ duyệt; lọc theo trạng thái.
        await Set(s.Tech, items[wbc], "6.1");
        await Set(s.Tech, items[note], "Mẫu hơi đục");
        (await Get(s.Tech, ListUrl("&status=cho-duyet"))).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("cho-duyet");
        (await Get(s.Tech, ListUrl("&status=cho-ket-qua"))).GetArrayLength().ShouldBe(0);
        (await Get(s.Tech, ListUrl("&status=da-duyet"))).GetArrayLength().ShouldBe(0);
        (await Get(s.Tech, ListUrl("&status="))).GetArrayLength().ShouldBe(1);
        (await Get(s.Tech, ListUrl($"&q={barcode}"))).GetArrayLength().ShouldBe(1);
        (await Get(s.Tech, ListUrl("&q=thi mau"))).GetArrayLength().ShouldBe(1);
        (await Get(s.Tech, ListUrl("&q=Không Có"))).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Nhap_tay_lam_tron_chuan_hoa_tinh_co_va_ghi_nguoi_nhap()
    {
        await using var s = await StartAsync("pkkqnhap");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0, unit: "g/L");
        await s.Tenant.Catalog.SaveRangesAsync(hgb,
        [
            new ReferenceRange(Guid.Empty, hgb, 'M', null, null, null, 130m, 170m, null, null, null),
            new ReferenceRange(Guid.Empty, hgb, 'F', null, null, null, 120m, 150m, null, null, null),
        ]);
        var free = await s.Service("TDO", "Hiệu giá Mẫu");
        var crp = await s.Service("CRP", "CRP Mẫu", decimals: 1);
        var (_, _, items) = await s.Order(Woman(), [hgb, free, crp]);

        await Set(s.Tech, items[hgb], "118,6");
        var stored = await s.Stored(items[hgb]);
        stored.Value.ShouldBe("119");
        stored.RefLow.ShouldBe(120m);
        stored.RefHigh.ShouldBe(150m);
        stored.Flag.ShouldBe("Low");
        stored.Source.ShouldBe("tay");
        stored.Status.ShouldBe("cho-duyet");
        stored.UpdatedBy.ShouldBe(s.TechId);
        var history = await s.History(items[hgb]);
        history.Single().ShouldSatisfyAllConditions(
            h => h.Action.ShouldBe("nhan"), h => h.OldValue.ShouldBeNull(), h => h.NewValue.ShouldBe("119"),
            h => h.Reason.ShouldBeNull(), h => h.ActorId.ShouldBe(s.TechId));

        // Chuẩn hoá khi chỉ số không khai số lẻ (ca normalizeNumber của result-value.spec.ts) và làm tròn theo số lẻ.
        foreach (var (raw, expected) in new[] { ("4,8", "4.8"), ("< 0,5", "<0.5"), ("1:4", "1:4") })
        {
            await Set(s.Admin, items[free], raw, "Sửa thử");
            (await s.Stored(items[free])).Value.ShouldBe(expected);
        }

        await Set(s.Head, items[crp], "12.46");
        (await s.Stored(items[crp])).Value.ShouldBe("12.5");
        (await s.History(items[crp])).Single().ActorId.ShouldBe(s.HeadId);
    }

    [Fact]
    public async Task Sua_gia_tri_can_ly_do_va_giu_lich_su()
    {
        await using var s = await StartAsync("pkkqsua");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (_, _, items) = await s.Order(Man(), [hgb]);
        await Set(s.Tech, items[hgb], "140");

        await ShouldFail(await Put(s.Tech, items[hgb], "145"), HttpStatusCode.BadRequest, "Nhập lý do sửa kết quả");
        await ShouldFail(await Put(s.Tech, items[hgb], "145", "   "), HttpStatusCode.BadRequest, "Nhập lý do sửa kết quả");
        (await s.Stored(items[hgb])).Value.ShouldBe("140");

        await Set(s.Head, items[hgb], "145", "  Nhập nhầm Thử ");
        (await s.Stored(items[hgb])).Value.ShouldBe("145");

        var history = await s.History(items[hgb]);
        history.Select(h => h.Action).ShouldBe(["nhan", "sua"]);
        history[1].ShouldSatisfyAllConditions(
            h => h.OldValue.ShouldBe("140"), h => h.NewValue.ShouldBe("145"), h => h.Reason.ShouldBe("Nhập nhầm Thử"), h => h.ActorId.ShouldBe(s.HeadId));

        var view = (await Get(s.Reception, $"/api/results/items/{items[hgb]}/history")).EnumerateArray().ToList();
        Props(view[0]).ShouldBe(["at", "action", "oldValue", "newValue", "reason", "actorName"]);
        view.Select(v => v.GetProperty("action").GetString()).ShouldBe(["sua", "nhan"]);
        view[0].GetProperty("oldValue").GetString().ShouldBe("140");
        view[0].GetProperty("newValue").GetString().ShouldBe("145");
        view[0].GetProperty("reason").GetString().ShouldBe("Nhập nhầm Thử");
        view[0].GetProperty("actorName").GetString().ShouldBe("tk Thử");
        view[0].GetProperty("at").GetString()!.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        view[1].GetProperty("actorName").GetString().ShouldBe("ktv Thử");
        view[1].GetProperty("oldValue").ValueKind.ShouldBe(JsonValueKind.Null);
        view[1].GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);

        // Dòng chỉ định chưa có kết quả / không tồn tại: lịch sử rỗng (như NestJS).
        (await Get(s.Tech, $"/api/results/items/{Guid.NewGuid()}/history")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Nhap_gia_tri_sai_thi_400_404()
    {
        await using var s = await StartAsync("pkkqsai");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var panel = await s.Service("CTM", "Công thức máu Mẫu", panel: true);
        var wbc = await s.Service("WBC", "Bạch cầu Mẫu", decimals: 2);
        await s.Tenant.Catalog.SetChildrenAsync(panel, [wbc]);
        var (_, _, items) = await s.Order(Man(), [hgb, panel]);

        await ShouldFail(await Put(s.Tech, items[hgb], " abc "), HttpStatusCode.BadRequest, "Giá trị không hợp lệ: 'abc'");
        await ShouldFail(await Put(s.Tech, items[hgb], "***.*"), HttpStatusCode.BadRequest, "Giá trị không hợp lệ: '***.*'");
        await ShouldFail(await Put(s.Tech, items[panel], "1"), HttpStatusCode.BadRequest, "Dịch vụ cha (panel) không có giá trị kết quả");
        await ShouldFail(await Put(s.Tech, Guid.NewGuid(), "1"), HttpStatusCode.NotFound, "Không tìm thấy dòng chỉ định");
        await ShouldFail(
            await s.Tech.PutAsJsonAsync("/api/results/items/khong-phai-uuid", new { value = "1", reason = (string?)null }, Ct),
            HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await s.Tech.PutAsJsonAsync($"/api/results/items/{items[hgb]}", new { reason = "Thử" }, Ct), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await s.Tech.PutAsJsonAsync($"/api/results/items/{items[hgb]}", new { value = 5 }, Ct), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await Put(s.Tech, items[hgb], new string('1', 501)), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await Put(s.Tech, items[hgb], "1", new string('x', 501)), HttpStatusCode.BadRequest, InvalidBody);
        (await s.Tech.PutAsJsonAsync($"/api/results/items/{items[hgb]}", new { value = "1", la = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await s.Scalar<int>("SELECT count(*)::int FROM result")).ShouldBe(0);

        // Ô trống: lưu rỗng (không phải "0"), không cần lý do.
        await Set(s.Tech, items[hgb], "   ");
        (await s.Stored(items[hgb])).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Quyen_theo_vai_tro_va_kiem_dau_vao_truoc_kiem_quyen()
    {
        await using var s = await StartAsync("pkkqquyen");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (sampleId, _, items) = await s.Order(Man(), [hgb]);

        // Tiếp nhận: xem được, không nhập / duyệt được.
        (await s.Reception.GetAsync(ListUrl(), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Reception.GetAsync($"/api/samples/{sampleId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.Reception.GetAsync($"/api/results/items/{items[hgb]}/history", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldFail(await Put(s.Reception, items[hgb], "140"), HttpStatusCode.Forbidden, EditorOnly);
        (await s.Scalar<int>("SELECT count(*)::int FROM result")).ShouldBe(0);

        await Set(s.Tech, items[hgb], "140");
        await ShouldFail(await Approve(s.Reception, sampleId), HttpStatusCode.Forbidden, EditorOnly);
        (await s.Sample(sampleId)).ApprovedAt.ShouldBeNull();

        (await Approve(s.Tech, sampleId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Kỹ thuật viên, Tiếp nhận không hủy duyệt được; mẫu vẫn đã duyệt.
        await ShouldFail(await Unapprove(s.Tech, sampleId, "Sửa Thử"), HttpStatusCode.Forbidden, UnapproverOnly);
        await ShouldFail(await Unapprove(s.Reception, sampleId, "Sửa Thử"), HttpStatusCode.Forbidden, UnapproverOnly);
        (await s.Sample(sampleId)).ApprovedAt.ShouldNotBeNull();
        (await s.History(items[hgb])).Select(h => h.Action).ShouldBe(["nhan", "duyet"]);

        // Kiểm tham số, rồi thân, rồi mới tới quyền.
        await ShouldFail(await s.Reception.PostAsync("/api/samples/khong-phai-uuid/approve", null, Ct), HttpStatusCode.BadRequest, BadUuid);
        await ShouldFail(await Unapprove(s.Tech, sampleId, ""), HttpStatusCode.BadRequest, "Nhập lý do");
        await ShouldFail(await Put(s.Reception, items[hgb], new string('1', 501)), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await s.Reception.PostAsync("/api/results/khong-phai-uuid/pending/accept", null, Ct), HttpStatusCode.BadRequest, BadUuid);
        var resultId = (await s.Stored(items[hgb])).Id;
        await ShouldFail(await s.Reception.PostAsync($"/api/results/{resultId}/pending/accept", null, Ct), HttpStatusCode.Forbidden, EditorOnly);
        await ShouldFail(await s.Reception.PostAsync($"/api/results/{resultId}/pending/reject", null, Ct), HttpStatusCode.Forbidden, EditorOnly);

        // Quyền trước tồn tại.
        await ShouldFail(await Approve(s.Reception, Guid.NewGuid()), HttpStatusCode.Forbidden, EditorOnly);
    }

    [Fact]
    public async Task Duyet_mau_va_cac_truong_hop_bien()
    {
        await using var s = await StartAsync("pkkqduyet");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var wbc = await s.Service("WBC", "Bạch cầu Mẫu", decimals: 2);

        // Chưa có giá trị nào → 409 theo ResultService.
        var (empty, _, _) = await s.Order(Man("Lê Văn Mẫu"), [hgb, wbc]);
        await ShouldFail(await Approve(s.Tech, empty), HttpStatusCode.Conflict, "Chưa có kết quả để duyệt");
        (await s.Sample(empty)).ApprovedAt.ShouldBeNull();

        // Còn thiếu kết quả: ResultService vẫn duyệt, chỉ những ô có giá trị thành đã duyệt.
        var (partial, _, items) = await s.Order(Man(), [hgb, wbc]);
        await Set(s.Tech, items[hgb], "140");
        (await Approve(s.Tech, partial)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var sample = await s.Sample(partial);
        sample.ApprovedAt.ShouldNotBeNull();
        sample.ApprovedBy.ShouldBe(s.TechId);
        (await s.Stored(items[hgb])).Status.ShouldBe("da-duyet");
        (await s.Scalar<int>("SELECT count(*)::int FROM result WHERE order_item_id = @id", new { id = items[wbc] })).ShouldBe(0);
        (await s.History(items[hgb])).Last().ShouldSatisfyAllConditions(
            h => h.Action.ShouldBe("duyet"), h => h.NewValue.ShouldBe("140"), h => h.ActorId.ShouldBe(s.TechId));

        var detail = await Get(s.Reception, $"/api/samples/{partial}");
        detail.GetProperty("approvedBy").GetString().ShouldBe("ktv Thử");
        DateTimeOffset.Parse(detail.GetProperty("approvedAt").GetString()!).ShouldBe(sample.ApprovedAt!.Value, TimeSpan.FromMilliseconds(1));
        (await Get(s.Tech, ListUrl("&status=da-duyet"))).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("da-duyet");

        // Duyệt lại mẫu đã duyệt: không làm gì (như NestJS và ResultService), không thêm lịch sử.
        (await Approve(s.Head, partial)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Sample(partial)).ApprovedBy.ShouldBe(s.TechId);
        (await s.History(items[hgb])).Count(h => h.Action == "duyet").ShouldBe(1);

        // Ô còn trống của mẫu đã duyệt cũng không nhập được.
        await ShouldFail(await Put(s.Tech, items[wbc], "5"), HttpStatusCode.Conflict, ApprovedLocked);

        await ShouldFail(await Approve(s.Tech, Guid.NewGuid()), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
        await ShouldFail(await s.Tech.GetAsync($"/api/samples/{Guid.NewGuid()}", Ct), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
        await ShouldFail(await s.Tech.GetAsync("/api/samples/khong-phai-uuid", Ct), HttpStatusCode.BadRequest, BadUuid);
    }

    [Fact]
    public async Task Sua_ket_qua_mau_da_duyet_bi_tu_choi()
    {
        await using var s = await StartAsync("pkkqkhoa");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (sampleId, _, items) = await s.Order(Man(), [hgb]);
        await Set(s.Tech, items[hgb], "140");
        (await Approve(s.Tech, sampleId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldFail(await Put(s.Admin, items[hgb], "150", "Sửa Thử"), HttpStatusCode.Conflict, ApprovedLocked);
        await ShouldFail(await Put(s.Head, items[hgb], "150"), HttpStatusCode.Conflict, ApprovedLocked);

        var stored = await s.Stored(items[hgb]);
        stored.Value.ShouldBe("140");
        stored.Status.ShouldBe("da-duyet");
        (await s.History(items[hgb])).Select(h => h.Action).ShouldBe(["nhan", "duyet"]);
    }

    [Fact]
    public async Task Huy_duyet_bat_buoc_ly_do()
    {
        await using var s = await StartAsync("pkkqhuy");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (sampleId, _, items) = await s.Order(Man(), [hgb]);
        await Set(s.Tech, items[hgb], "140");
        (await Approve(s.Tech, sampleId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldFail(await Unapprove(s.Head, sampleId, ""), HttpStatusCode.BadRequest, "Nhập lý do");
        await ShouldFail(await Unapprove(s.Head, sampleId, "   "), HttpStatusCode.BadRequest, "Nhập lý do hủy duyệt");
        await ShouldFail(await Unapprove(s.Head, sampleId, null), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await Unapprove(s.Head, sampleId, new string('x', 501)), HttpStatusCode.BadRequest, InvalidBody);
        await ShouldFail(await s.Head.PostAsync($"/api/samples/{sampleId}/unapprove", null, Ct), HttpStatusCode.BadRequest, InvalidBody);
        (await s.Sample(sampleId)).ApprovedAt.ShouldNotBeNull();

        (await Unapprove(s.Head, sampleId, "  Sai tên Thử ")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var sample = await s.Sample(sampleId);
        sample.ApprovedAt.ShouldBeNull();
        sample.ApprovedBy.ShouldBeNull();
        (await s.Stored(items[hgb])).Status.ShouldBe("cho-duyet");
        (await s.History(items[hgb])).Last().ShouldSatisfyAllConditions(
            h => h.Action.ShouldBe("huy-duyet"), h => h.Reason.ShouldBe("Sai tên Thử"), h => h.ActorId.ShouldBe(s.HeadId), h => h.NewValue.ShouldBe("140"));

        // Mẫu chưa duyệt: không làm gì. Quản trị cũng hủy duyệt được.
        (await Unapprove(s.Admin, sampleId, "Thử")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.History(items[hgb])).Count(h => h.Action == "huy-duyet").ShouldBe(1);
        (await Approve(s.Tech, sampleId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Unapprove(s.Admin, sampleId, "Thử lại")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.History(items[hgb])).Last().ActorId.ShouldBe(s.AdminId);
        await ShouldFail(await Unapprove(s.Admin, Guid.NewGuid(), "Thử"), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
    }

    /// <summary>Máy gửi (tự duyệt), máy gửi lại giá trị khác cho mẫu đã duyệt (chờ thay), bỏ, rồi chấp nhận — đúng đường IngestService.</summary>
    [Fact]
    public async Task Cho_thay_chap_nhan_va_bo_ghi_lich_su()
    {
        await using var s = await StartAsync("pkkqthay");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0, autoApprove: true);
        var (sampleId, barcode, items) = await s.Order(Man(), [hgb]);
        var instrument = await s.Tenant.Instruments.SaveAsync(new InstrumentInfo(Guid.Empty, "Máy Thử", null, null, null, null, null, true));
        await s.Tenant.Instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrument, "HGB", hgb, null, null, false, false));
        Task Send(string value) => s.Tenant.Ingest.IngestAsync(new InstrumentReading(
            instrument, barcode, false, DateTimeOffset.UtcNow, "raw-thu", [new ReadingItem("HGB", value, null, null)]));

        await Send("140");
        var detail = await Get(s.Tech, $"/api/samples/{sampleId}");
        detail.GetProperty("approvedBy").GetString().ShouldBe("Tự duyệt");

        await Send("151");
        var stored = await s.Stored(items[hgb]);
        stored.Value.ShouldBe("140");
        stored.PendingValue.ShouldBe("151");
        (await Get(s.Tech, ListUrl())).EnumerateArray().Single().GetProperty("hasPending").GetBoolean().ShouldBeTrue();
        var row = (await Get(s.Tech, $"/api/samples/{sampleId}")).GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("pendingValue").GetString().ShouldBe("151");
        row.GetProperty("source").GetString().ShouldBe("may");
        var resultId = stored.Id;

        // Bỏ: giữ giá trị đã duyệt, mẫu vẫn đã duyệt.
        (await s.Tech.PostAsync($"/api/results/{resultId}/pending/reject", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stored = await s.Stored(items[hgb]);
        stored.Value.ShouldBe("140");
        stored.PendingValue.ShouldBeNull();
        (await s.Sample(sampleId)).ApprovedAt.ShouldNotBeNull();
        (await s.History(items[hgb])).Last().ShouldSatisfyAllConditions(
            h => h.Action.ShouldBe("bo-thay-the"), h => h.OldValue.ShouldBe("151"), h => h.NewValue.ShouldBe("140"), h => h.ActorId.ShouldBe(s.TechId));
        await ShouldFail(await s.Tech.PostAsync($"/api/results/{resultId}/pending/reject", null, Ct), HttpStatusCode.Conflict, "Không có bản thay thế để bỏ");
        await ShouldFail(await s.Tech.PostAsync($"/api/results/{resultId}/pending/accept", null, Ct), HttpStatusCode.Conflict, "Không có bản thay thế để chấp nhận");

        // Chấp nhận: giá trị máy gửi lại thành chính thức, mẫu về chưa duyệt, lịch sử thay-the do người chấp nhận. ResultService
        // đặt kết quả này về cho-duyet TRƯỚC khi hủy duyệt mẫu, nên dòng huy-duyet chỉ ghi cho các kết quả khác còn đang duyệt
        // (mẫu một chỉ số thì không có dòng huy-duyet) — giống hệt bản NestJS.
        await Send("152");
        (await s.Head.PostAsync($"/api/results/{resultId}/pending/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stored = await s.Stored(items[hgb]);
        stored.Value.ShouldBe("152");
        stored.PendingValue.ShouldBeNull();
        stored.Status.ShouldBe("cho-duyet");
        stored.UpdatedBy.ShouldBe(s.HeadId);
        (await s.Sample(sampleId)).ApprovedAt.ShouldBeNull();
        var history = await s.History(items[hgb]);
        history.TakeLast(2).Select(h => (h.Action, h.OldValue, h.NewValue, h.Reason, h.ActorId)).ShouldBe(
        [
            ("thay-the", "140", "152", "Máy gửi lại kết quả cho mẫu đã duyệt", (Guid?)null),
            ("thay-the", "140", "152", null, s.HeadId),
        ]);

        var view = (await Get(s.Tech, $"/api/results/items/{items[hgb]}/history")).EnumerateArray().ToList();
        view.Select(v => v.GetProperty("action").GetString()).ShouldBe(
            ["thay-the", "thay-the", "bo-thay-the", "thay-the", "duyet", "nhan"]);
        view[0].GetProperty("actorName").GetString().ShouldBe("tk Thử");
        view.Last().GetProperty("actorName").ValueKind.ShouldBe(JsonValueKind.Null);

        await ShouldFail(await s.Tech.PostAsync($"/api/results/{Guid.NewGuid()}/pending/accept", null, Ct), HttpStatusCode.NotFound, "Không tìm thấy kết quả");
        await ShouldFail(await s.Tech.PostAsync($"/api/results/{Guid.NewGuid()}/pending/reject", null, Ct), HttpStatusCode.NotFound, "Không tìm thấy kết quả");
    }

    [Fact]
    public async Task Phong_kham_khac_khong_thay_duoc_mau()
    {
        await using var a = await StartAsync("pkkqa");
        var hgb = await a.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (sampleId, _, items) = await a.Order(Man(), [hgb]);
        await Set(a.Tech, items[hgb], "140");
        var resultId = (await a.Stored(items[hgb])).Id;

        var clinicB = await a.Host.AddClinicAsync("pkkqb", "Phòng khám Thử B");
        await a.Host.AddUserAsync(clinicB, "admin", Password, "quan-tri");
        var b = await a.Host.LoginAsync("pkkqb", "admin", Password);

        (await Get(b, ListUrl())).GetArrayLength().ShouldBe(0);
        await ShouldFail(await b.GetAsync($"/api/samples/{sampleId}", Ct), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
        await ShouldFail(await Approve(b, sampleId), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
        await ShouldFail(await Unapprove(b, sampleId, "Thử"), HttpStatusCode.NotFound, "Không tìm thấy mẫu");
        await ShouldFail(await Put(b, items[hgb], "999", "Thử"), HttpStatusCode.NotFound, "Không tìm thấy dòng chỉ định");
        await ShouldFail(await b.PostAsync($"/api/results/{resultId}/pending/accept", null, Ct), HttpStatusCode.NotFound, "Không tìm thấy kết quả");
        await ShouldFail(await b.PostAsync($"/api/results/{resultId}/pending/reject", null, Ct), HttpStatusCode.NotFound, "Không tìm thấy kết quả");
        (await Get(b, $"/api/results/items/{items[hgb]}/history")).GetArrayLength().ShouldBe(0);

        var stored = await a.Stored(items[hgb]);
        stored.Value.ShouldBe("140");
        stored.Status.ShouldBe("cho-duyet");
        (await a.Sample(sampleId)).ApprovedAt.ShouldBeNull();
        (await a.History(items[hgb])).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Loc_ngay_theo_gio_phong_kham_o_ranh_gioi_nua_dem()
    {
        await using var s = await StartAsync("pkkqngay");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var (lateEvening, _, _) = await s.Order(Man("Nguyễn Văn Tối Mẫu"), [hgb]);
        var (earlyMorning, _, _) = await s.Order(Man("Lê Văn Sáng Mẫu"), [hgb]);
        // 23:59:59 ngày 15/11 và 00:00:00 ngày 16/11 giờ Việt Nam (UTC+7).
        const string setCreated = "UPDATE lab_order SET created_at = @at WHERE id = (SELECT order_id FROM sample WHERE id = @sampleId)";
        await s.Exec(setCreated, new { at = new DateTimeOffset(2026, 11, 15, 16, 59, 59, TimeSpan.Zero), sampleId = lateEvening });
        await s.Exec(setCreated, new { at = new DateTimeOffset(2026, 11, 15, 17, 0, 0, TimeSpan.Zero), sampleId = earlyMorning });

        async Task<string?[]> Ids(HttpClient client, string from, string to) =>
            (await Get(client, $"/api/samples?from={from}&to={to}")).EnumerateArray().Select(x => x.GetProperty("sampleId").GetString()).ToArray();

        (await Ids(s.Tech, "2026-11-15", "2026-11-15")).ShouldBe([lateEvening.ToString()]);
        (await Ids(s.Tech, "2026-11-16", "2026-11-16")).ShouldBe([earlyMorning.ToString()]);
        (await Ids(s.Tech, "2026-11-15", "2026-11-16")).ShouldBe([earlyMorning.ToString(), lateEvening.ToString()]);
        (await Ids(s.Tech, "2026-11-14", "2026-11-14")).ShouldBeEmpty();

        // Khoảng ngày / trạng thái sai.
        foreach (var url in new[]
        {
            "/api/samples", "/api/samples?from=2026-11-15", "/api/samples?from=2026-11-16&to=2026-11-15",
            "/api/samples?from=15/11/2026&to=2026-11-15", "/api/samples?from=2026-11-15&to=2026-11-15x",
            "/api/samples?from=2026-02-30&to=2026-03-01",
        })
        {
            await ShouldFail(await s.Tech.GetAsync(url, Ct), HttpStatusCode.BadRequest, BadRange);
        }

        await ShouldFail(await s.Tech.GetAsync("/api/samples?from=2026-11-15&to=2026-11-15&status=sai", Ct), HttpStatusCode.BadRequest, "Trạng thái không hợp lệ");

        // Cùng CSDL, server cấu hình phòng khám ở UTC+0: hai mẫu cùng thuộc ngày 15/11 — "ngày" theo cấu hình, không theo máy chủ.
        await using var utc = await ApiHost.StartAsync(fixture, settings: new Dictionary<string, string> { ["LocalUtcOffsetMinutes"] = "0" });
        var tech = await utc.LoginAsync("pkkqngay", "ktv", Password);
        (await Ids(tech, "2026-11-15", "2026-11-15")).ShouldBe([earlyMorning.ToString(), lateEvening.ToString()]);
        (await Ids(tech, "2026-11-16", "2026-11-16")).ShouldBeEmpty();
    }

    /// <summary>
    /// Hai người bấm Duyệt cùng lúc: cả hai nhận 204 (duyệt mẫu đã duyệt là không làm gì — như NestJS), nhưng chỉ đúng một
    /// lần duyệt được ghi: một dòng lịch sử <c>duyet</c> cho mỗi kết quả, người duyệt trên mẫu trùng người trong lịch sử.
    /// </summary>
    [Fact]
    public async Task Hai_request_duyet_dong_thoi_chi_mot_lan_duyet_duoc_ghi()
    {
        await using var s = await StartAsync("pkkqdua");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);
        var wbc = await s.Service("WBC", "Bạch cầu Mẫu", decimals: 2);

        for (var round = 0; round < 10; round++)
        {
            var (sampleId, _, items) = await s.Order(Man($"Mẫu Đua {round}"), [hgb, wbc]);
            await Set(s.Tech, items[hgb], "140");
            await Set(s.Tech, items[wbc], "6.5");

            var responses = await Task.WhenAll(Approve(s.Tech, sampleId), Approve(s.Head, sampleId));
            responses.Select(r => r.StatusCode).ShouldAllBe(code => code == HttpStatusCode.NoContent);

            var sample = await s.Sample(sampleId);
            sample.ApprovedAt.ShouldNotBeNull();
            new[] { s.TechId, s.HeadId }.ShouldContain(sample.ApprovedBy!.Value);
            foreach (var item in items.Values)
            {
                var approvals = (await s.History(item)).Where(h => h.Action == "duyet").ToList();
                approvals.Count.ShouldBe(1);
                approvals[0].ActorId.ShouldBe(sample.ApprovedBy);
                approvals[0].At.ShouldBe(sample.ApprovedAt!.Value);
                (await s.Stored(item)).Status.ShouldBe("da-duyet");
            }
        }
    }

    /// <summary>
    /// Sửa giá trị chạy đua với Duyệt: hoặc sửa xong rồi mới duyệt (duyệt đúng giá trị mới), hoặc duyệt trước và lần sửa bị
    /// từ chối 409 — không bao giờ có mẫu đã duyệt mà giá trị đổi sau khi duyệt, hay giá trị đổi mà không có lịch sử.
    /// </summary>
    [Fact]
    public async Task Sua_gia_tri_chay_dua_voi_duyet_khong_de_lai_mau_duyet_bi_doi()
    {
        await using var s = await StartAsync("pkkqdua2");
        var hgb = await s.Service("HGB", "Huyết sắc tố Mẫu", decimals: 0);

        for (var round = 0; round < 12; round++)
        {
            var (sampleId, _, items) = await s.Order(Man($"Mẫu Đua Sửa {round}"), [hgb]);
            var item = items[hgb];
            await Set(s.Tech, item, "100");

            var edit = Put(s.Admin, item, "120", "Sửa Thử");
            var approve = Approve(s.Head, sampleId);
            await Task.WhenAll(edit, approve);

            approve.Result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var edited = edit.Result.StatusCode == HttpStatusCode.NoContent;
            if (!edited)
            {
                await ShouldFail(edit.Result, HttpStatusCode.Conflict, ApprovedLocked);
            }

            var stored = await s.Stored(item);
            stored.Status.ShouldBe("da-duyet");
            stored.Value.ShouldBe(edited ? "120" : "100");
            (await s.Sample(sampleId)).ApprovedAt.ShouldNotBeNull();

            var history = await s.History(item);
            history.Select(h => h.Action).ShouldBe(edited ? ["nhan", "sua", "duyet"] : ["nhan", "duyet"]);
            history.Last().NewValue.ShouldBe(stored.Value);
        }
    }
}
