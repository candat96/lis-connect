using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Xem, giải mã thử và giải mã lại bản tin thô trên <c>/api/raw-messages</c>.</summary>
public sealed class RawMessagesApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task ShouldFail(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        response.StatusCode.ShouldBe(status);
        (await RawEnv.JsonOf(response)).GetProperty("message").GetString().ShouldBe(message);
    }

    [Fact]
    public async Task Redecode_chi_quan_tri_truong_khoa_va_ghi_vet_nguoi()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba1");
        var id = await env.StoreAsync();
        (await env.PostAgentAsync(null, env.AgentMessage(Guid.NewGuid(), RawEnv.Hl7("KHONGCO0003"), seq: 9))).EnsureSuccessStatusCode();
        await env.Tenant.RawProcessor.DecodeAsync(id, "tu-dong", null, Ct);

        foreach (var denied in new[] { "ktv", "tiepnhan" })
        {
            var client = await env.LoginAsync(denied);
            (await client.PostAsJsonAsync("/api/raw-messages/redecode", new { ids = new[] { id } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync($"/api/raw-messages/{id}/preview", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        foreach (var allowed in new[] { "truongkhoa", "admin" })
        {
            var client = await env.LoginAsync(allowed);
            var response = await client.PostAsJsonAsync("/api/raw-messages/redecode", new { ids = new[] { id } }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await RawEnv.JsonOf(response);
            body.GetProperty("count").GetInt32().ShouldBe(1);
            body.GetProperty("errors").GetInt32().ShouldBe(0);
            // Giá trị trùng với lượt trước → không ghi đè, không nhân đôi.
            body.GetProperty("stored").GetInt32().ShouldBe(0);
            body.GetProperty("unchanged").GetInt32().ShouldBeGreaterThan(0);
        }

        var actors = await env.Scalar<int>(
            "SELECT count(DISTINCT actor_id)::int FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'giai-ma-lai' AND decoder_version <> ''", new { id });
        actors.ShouldBe(2);
        var truongKhoa = await env.UserIdAsync("truongkhoa");
        (await env.Scalar<int>(
            "SELECT count(*)::int FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'giai-ma-lai' AND actor_id = @truongKhoa",
            new { id, truongKhoa })).ShouldBe(1);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'tu-dong'", new { id })).ShouldBe(1);
    }

    [Fact]
    public async Task Redecode_theo_khoang_chay_tuan_tu_va_bao_loi_khong_tim_thay_hoac_dang_chay()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba2");
        var admin = await env.LoginAsync("admin");
        var id = await env.StoreAsync();
        await env.Tenant.RawProcessor.DecodeAsync(id, "tu-dong", null, Ct);

        var range = await admin.PostAsJsonAsync("/api/raw-messages/redecode",
            new { instrumentId = env.InstrumentId, from = RawEnv.CompletedAt.AddHours(-1), to = RawEnv.CompletedAt.AddHours(1) }, Ct);
        range.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RawEnv.JsonOf(range)).GetProperty("count").GetInt32().ShouldBe(1);

        await ShouldFail(await admin.PostAsJsonAsync("/api/raw-messages/redecode", new { ids = new[] { Guid.NewGuid() } }, Ct),
            HttpStatusCode.NotFound, "Không tìm thấy bản tin");
        await ShouldFail(await admin.PostAsJsonAsync($"/api/raw-messages/{Guid.NewGuid()}/preview", new { }, Ct),
            HttpStatusCode.NotFound, "Không tìm thấy bản tin");
        await ShouldFail(await admin.PostAsJsonAsync("/api/raw-messages/redecode", new { }, Ct), HttpStatusCode.BadRequest, "Dữ liệu gửi lên không hợp lệ");

        await env.Exec("UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = now(), decode_kind = 'tu-dong' WHERE id = @id", new { id });
        await ShouldFail(await admin.PostAsJsonAsync("/api/raw-messages/redecode", new { ids = new[] { id } }, Ct),
            HttpStatusCode.Conflict, "Bản tin đang được giải mã");
    }

    [Fact]
    public async Task Redecode_qua_500_bao_loi()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba3");
        var admin = await env.LoginAsync("admin");
        const string message = "Tối đa 500 bản tin mỗi lần — thu hẹp khoảng thời gian";

        await ShouldFail(
            await admin.PostAsJsonAsync("/api/raw-messages/redecode", new { ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() }, Ct),
            HttpStatusCode.BadRequest, message);

        await env.Exec(
            """
            INSERT INTO raw_message (id, instrument_id, seq, session, started_at, completed_at, bytes, partial, decode_status)
            SELECT gen_random_uuid(), @instrument, g, 's', @at, @at + g * interval '1 second', '\x00'::bytea, false, 'chi-luu'
            FROM generate_series(1, 501) g
            """,
            new { instrument = env.InstrumentId, at = RawEnv.CompletedAt.UtcDateTime });
        await ShouldFail(
            await admin.PostAsJsonAsync("/api/raw-messages/redecode",
                new { instrumentId = env.InstrumentId, from = RawEnv.CompletedAt.AddHours(-1), to = RawEnv.CompletedAt.AddHours(1) }, Ct),
            HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task Preview_khong_ghi_gi()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba4");
        var id = await env.StoreAsync();
        var admin = await env.LoginAsync("admin");

        var response = await admin.PostAsJsonAsync($"/api/raw-messages/{id}/preview", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        var reading = body.GetProperty("readings").EnumerateArray().Single();
        reading.GetProperty("barcode").GetString().ShouldBe(env.Barcode);
        reading.GetProperty("isQc").GetBoolean().ShouldBeFalse();
        var items = reading.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("channelCode").GetString()).ShouldBe(["6690-2", "718-7"]);
        items[0].GetProperty("rawValue").GetString().ShouldBe("9.81");
        items[0].GetProperty("unit").GetString().ShouldBe("10*9/L");
        body.GetProperty("abandoned").GetBoolean().ShouldBeFalse();
        body.TryGetProperty("queries", out _).ShouldBeTrue();
        body.TryGetProperty("notes", out _).ShouldBeTrue();

        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @id", new { id })).ShouldBe("cho-giai-ma");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode")).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM result")).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM result_history")).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM qc_result")).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM unmatched_result")).ShouldBe(0);
    }

    [Fact]
    public async Task List_loc_trang_thai_va_phan_trang()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba5");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await env.StoreAsync(completedAt: RawEnv.CompletedAt.AddMinutes(i), seq: i + 1));
        }

        await env.Exec("UPDATE raw_message SET decode_status = 'loi', decode_result = '{\"error\":\"Hỏng thử\"}'::jsonb WHERE id = ANY(@bad)", new { bad = new[] { ids[0], ids[2], ids[4] } });
        await env.Exec("UPDATE raw_message SET decode_status = 'can-xem-lai' WHERE id = @id", new { id = ids[3] });
        var ktv = await env.LoginAsync("ktv");

        // Cả kỹ thuật viên cũng xem được; mới nhất trước, đủ trường.
        var all = await ktv.GetFromJsonAsync<JsonElement>("/api/raw-messages", Ct);
        all.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe(ids.AsEnumerable().Reverse());
        var top = all[0];
        top.GetProperty("instrumentId").GetGuid().ShouldBe(env.InstrumentId);
        top.GetProperty("instrumentName").GetString().ShouldBe("Máy thử BC-01");
        top.GetProperty("seq").GetInt64().ShouldBe(5);
        top.GetProperty("session").GetString().ShouldBe("phien-thu");
        top.GetProperty("partial").GetBoolean().ShouldBeFalse();
        top.GetProperty("size").GetInt32().ShouldBe(RawEnv.Hl7(env.Barcode).Length);
        top.GetProperty("decodeStatus").GetString().ShouldBe("loi");
        top.GetProperty("summary").GetProperty("error").GetString().ShouldBe("Hỏng thử");
        top.TryGetProperty("startedAt", out _).ShouldBeTrue();
        top.TryGetProperty("completedAt", out _).ShouldBeTrue();
        top.TryGetProperty("decoderVersion", out _).ShouldBeTrue();

        var filtered = await ktv.GetFromJsonAsync<JsonElement>("/api/raw-messages?status=loi,can-xem-lai", Ct);
        filtered.GetArrayLength().ShouldBe(4);
        var onlyBad = await ktv.GetFromJsonAsync<JsonElement>("/api/raw-messages?status=loi", Ct);
        onlyBad.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([ids[4], ids[2], ids[0]]);
        var other = await ktv.GetFromJsonAsync<JsonElement>($"/api/raw-messages?instrumentId={env.OtherInstrumentId}", Ct);
        other.GetArrayLength().ShouldBe(0);

        // Phân trang theo con trỏ before = "{completedAt}|{id}" của dòng cuối trang trước.
        var page1 = await ktv.GetFromJsonAsync<JsonElement>("/api/raw-messages?limit=2", Ct);
        page1.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([ids[4], ids[3]]);
        var last = page1[1];
        var cursor = Uri.EscapeDataString($"{last.GetProperty("completedAt").GetString()}|{last.GetProperty("id").GetGuid()}");
        var page2 = await ktv.GetFromJsonAsync<JsonElement>($"/api/raw-messages?limit=2&before={cursor}", Ct);
        page2.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([ids[2], ids[1]]);
        var from = Uri.EscapeDataString(RawEnv.CompletedAt.AddMinutes(1).UtcDateTime.ToString("O"));
        var to = Uri.EscapeDataString(RawEnv.CompletedAt.AddMinutes(3).UtcDateTime.ToString("O"));
        (await ktv.GetFromJsonAsync<JsonElement>($"/api/raw-messages?from={from}&to={to}", Ct)).GetArrayLength().ShouldBe(2);

        (await ktv.GetAsync("/api/raw-messages?status=khong-co", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await env.Host.Anonymous.GetAsync("/api/raw-messages", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Chi_tiet_co_hex_ket_qua_va_dau_vet()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "ba6");
        var id = await env.StoreAsync();
        await env.Tenant.RawProcessor.DecodeAsync(id, "tu-dong", null, Ct);
        var ktv = await env.LoginAsync("ktv");

        var detail = await ktv.GetFromJsonAsync<JsonElement>($"/api/raw-messages/{id}", Ct);

        detail.GetProperty("id").GetGuid().ShouldBe(id);
        detail.GetProperty("decodeStatus").GetString().ShouldBe("da-giai-ma");
        detail.GetProperty("hex").GetString()!.ShouldStartWith("0B4D5348");
        detail.GetProperty("size").GetInt32().ShouldBe(RawEnv.Hl7(env.Barcode).Length);
        detail.GetProperty("decodeResult").GetProperty("stored").GetInt32().ShouldBe(2);
        var decodes = detail.GetProperty("decodes").EnumerateArray().ToList();
        decodes.Count.ShouldBe(1);
        decodes[0].GetProperty("kind").GetString().ShouldBe("tu-dong");
        decodes[0].GetProperty("status").GetString().ShouldBe("da-giai-ma");
        decodes[0].GetProperty("actorName").ValueKind.ShouldBe(JsonValueKind.Null);
        decodes[0].GetProperty("result").GetProperty("readings").GetInt32().ShouldBe(1);

        (await ktv.GetAsync($"/api/raw-messages/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ktv.GetAsync("/api/raw-messages/khong-phai-uuid", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Hex chỉ lấy 256 KB đầu.
        var bigId = await env.StoreAsync(new byte[300 * 1024], seq: 2);
        var big = await ktv.GetFromJsonAsync<JsonElement>($"/api/raw-messages/{bigId}", Ct);
        big.GetProperty("hex").GetString()!.Length.ShouldBe(256 * 1024 * 2);
        big.GetProperty("size").GetInt32().ShouldBe(300 * 1024);
        big.GetProperty("hexTruncated").GetBoolean().ShouldBeTrue();
    }
}
