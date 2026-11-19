using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lis.Server.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Agent gửi bản tin thô lên <c>POST /agent/v1/raw-messages</c>: lưu, chống trùng, giải mã ở server, cách ly phòng khám, phục hồi.</summary>
public sealed class RawMessagesAgentApiTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Result(JsonElement body, int index = 0) => body.GetProperty("results")[index];

    [Fact]
    public async Task Gui_lo_luu_va_giai_ma_tra_da_luu()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt1");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var response = await env.PostAgentAsync(null, env.AgentMessage(first, seq: 1), env.AgentMessage(second, RawEnv.Hl7("KHONGCO0001"), seq: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        body.GetProperty("results").GetArrayLength().ShouldBe(2);
        Result(body).GetProperty("id").GetGuid().ShouldBe(first);
        Result(body).GetProperty("status").GetString().ShouldBe("da-luu");
        Result(body).GetProperty("decodeStatus").GetString().ShouldBe("da-giai-ma");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("9.81");
        (await env.ValueOfAsync(env.HgbItemId)).ShouldBe("132");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(2);
        (await env.Scalar<int>("SELECT count(*)::int FROM unmatched_result WHERE raw_ref = @r", new { r = $"ban-tin:{second}" })).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Gui_lai_cung_id_tra_trung_khong_ingest_lan_hai()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt2");
        var id = Guid.NewGuid();
        (await env.PostAgentAsync(null, env.AgentMessage(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var history = await env.Scalar<int>("SELECT count(*)::int FROM result_history");
        history.ShouldBeGreaterThan(0);

        var again = await env.PostAgentAsync(null, env.AgentMessage(id));

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(again);
        Result(body).GetProperty("status").GetString().ShouldBe("trung");
        Result(body).GetProperty("decodeStatus").GetString().ShouldBe("da-giai-ma");
        (await env.Scalar<int>("SELECT count(*)::int FROM result_history")).ShouldBe(history);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(1);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode")).ShouldBe(1);
    }

    [Fact]
    public async Task May_khong_thuoc_agent_khong_luu()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt3");

        var response = await env.PostAgentAsync(null,
            env.AgentMessage(Guid.NewGuid(), instrumentId: env.OtherInstrumentId),
            env.AgentMessage(Guid.NewGuid(), instrumentId: Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        Result(body).GetProperty("status").GetString().ShouldBe("khong-thuoc-agent");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("khong-thuoc-agent");
        Result(body).GetProperty("decodeStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(0);
    }

    [Fact]
    public async Task Agent_phong_kham_khac_401_hoac_khong_thay()
    {
        await using var a = await RawEnv.CreateAsync(fixture, "bt4a");
        await using var b = await RawEnv.CreateAsync(fixture, "bt4b", a.Host);

        // Khoá sai → 401.
        (await a.PostAgentAsync(a.AgentKey + "x", a.AgentMessage(Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await a.Host.AgentClient().PostAsJsonAsync("/agent/v1/raw-messages", new { messages = Array.Empty<object>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Agent phòng khám B gửi bản tin cho máy của phòng khám A → không thấy máy đó, không lưu vào A.
        var response = await b.PostAgentAsync(null, a.AgentMessage(Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Result(await RawEnv.JsonOf(response)).GetProperty("status").GetString().ShouldBe("khong-thuoc-agent");
        (await a.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(0);
        (await b.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(0);
        (await a.ValueOfAsync(a.WbcItemId)).ShouldBeNull();
    }

    [Fact]
    public async Task Ban_tin_qua_4MB_khong_hop_le()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt5");
        var big = Guid.NewGuid();
        var ok = Guid.NewGuid();

        var response = await env.PostAgentAsync(null,
            env.AgentMessage(big, new byte[4 * 1024 * 1024 + 1]),
            env.AgentMessage(ok, seq: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        Result(body).GetProperty("status").GetString().ShouldBe("khong-hop-le");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @big", new { big })).ShouldBe(0);

        // Đúng 4 MB thì được lưu; base64 hỏng, thiếu trường, rỗng thì khong-hop-le.
        var exact = Guid.NewGuid();
        var mixed = await env.PostAgentAsync(null,
            env.AgentMessage(exact, new byte[4 * 1024 * 1024]),
            new { id = Guid.NewGuid(), instrumentId = env.InstrumentId, seq = 3, session = "s", startedAt = RawEnv.CompletedAt, completedAt = RawEnv.CompletedAt, bytes = "!!không-phải-base64!!", partial = false },
            new { id = Guid.NewGuid(), instrumentId = env.InstrumentId },
            env.AgentMessage(Guid.NewGuid(), []));
        var mixedBody = await RawEnv.JsonOf(mixed);
        Result(mixedBody).GetProperty("status").GetString().ShouldBe("da-luu");
        Result(mixedBody, 1).GetProperty("status").GetString().ShouldBe("khong-hop-le");
        Result(mixedBody, 2).GetProperty("status").GetString().ShouldBe("khong-hop-le");
        Result(mixedBody, 3).GetProperty("status").GetString().ShouldBe("khong-hop-le");
    }

    [Fact]
    public async Task Lo_qua_50_ban_tin_bi_tu_choi()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt5b");
        var messages = Enumerable.Range(1, 51).Select(i => env.AgentMessage(Guid.NewGuid(), seq: i)).ToArray();

        var response = await env.PostAgentAsync(null, messages);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_loi_van_200_da_luu()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt6");
        // Cấu hình driver hỏng → bộ giải mã ném lỗi; bản tin vẫn phải được lưu và agent nhận 200.
        await env.Exec("UPDATE instrument SET protocol_family = 'beckman-au', driver_config = '\"khong-phai-doi-tuong\"'::jsonb WHERE id = @id", new { id = env.InstrumentId });
        var id = Guid.NewGuid();

        var response = await env.PostAgentAsync(null, env.AgentMessage(id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        Result(body).GetProperty("status").GetString().ShouldBe("da-luu");
        Result(body).GetProperty("decodeStatus").GetString().ShouldBe("loi");
        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @id", new { id })).ShouldBe("loi");
    }

    [Fact]
    public async Task Khoi_dong_lai_vot_cho_giai_ma()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt7");
        var waiting = await env.StoreAsync(seq: 1);
        // Lượt tự động chết giữa chừng từ lâu (không còn giao dịch nào giữ dòng).
        var stuck = await env.StoreAsync(RawEnv.Hl7("KHONGCO0002"), seq: 2);
        await env.Exec(
            "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = now() - interval '10 minutes', decode_kind = 'tu-dong', decode_attempts = 1 WHERE id = @stuck",
            new { stuck });

        await env.Host.RawWorker.RecoverAllAsync(Ct);

        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @waiting", new { waiting })).ShouldBe("da-giai-ma");
        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @stuck", new { stuck })).ShouldBe("da-giai-ma");
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("9.81");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode WHERE raw_message_id = @waiting", new { waiting })).ShouldBe(1);
    }

    [Fact]
    public async Task Worker_don_ban_tin_cu_hon_90_ngay_tru_ban_tin_chua_xong()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt8");
        var done = await env.StoreAsync(seq: 1);
        var waiting = await env.StoreAsync(seq: 2);
        var fresh = await env.StoreAsync(seq: 3);
        await env.Exec("UPDATE raw_message SET decode_status = 'da-giai-ma' WHERE id = @done OR id = @fresh", new { done, fresh });
        await env.Exec("UPDATE raw_message SET stored_at = now() - interval '91 days' WHERE id = @done OR id = @waiting", new { done, waiting });

        await env.Host.RawWorker.PurgeAllAsync(Ct);

        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @done", new { done })).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @waiting", new { waiting })).ShouldBe(1);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @fresh", new { fresh })).ShouldBe(1);
    }

    [Fact]
    public async Task Readings_cu_404()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt9");
        var client = env.Host.AgentClient(env.AgentKey);

        var response = await client.PostAsJsonAsync("/agent/v1/readings", new { instrumentId = env.InstrumentId, barcode = env.Barcode }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var connection = new NpgsqlConnection(env.Fixture.MasterConnection);
        (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('public.agent_reading') IS NULL")).ShouldBeTrue();
    }

    private const string FailTrigger =
        """
        CREATE FUNCTION raw_message_thu() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.session = 'loi-vinh-vien' THEN RAISE EXCEPTION 'thu' USING ERRCODE = '22001';
            ELSIF NEW.session = 'rang-buoc' THEN RAISE EXCEPTION 'thu' USING ERRCODE = '23514';
            ELSIF NEW.session = 'loi-tam' THEN RAISE EXCEPTION 'thu' USING ERRCODE = '40001';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER raw_message_thu_trg BEFORE INSERT ON raw_message FOR EACH ROW EXECUTE FUNCTION raw_message_thu();
        """;

    [Fact]
    public async Task Lo_vuot_16MB_413_va_khong_luu_ban_tin_nao()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt10");
        // 5 bản tin 4 MB hợp lệ từng cái, tổng 20 MB: phải bị từ chối TRƯỚC khi lưu cái nào.
        var messages = Enumerable.Range(1, 5).Select(i => env.AgentMessage(Guid.NewGuid(), new byte[4 * 1024 * 1024], seq: i)).ToArray();

        var response = await env.PostAgentAsync(null, messages);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(0);
    }

    [Fact]
    public async Task Session_co_NUL_khong_hop_le_khong_ket_lo()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt11");
        var ok = Guid.NewGuid();

        var response = await env.PostAgentAsync(null, env.AgentMessage(Guid.NewGuid(), session: "pha\0in"), env.AgentMessage(ok, seq: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        Result(body).GetProperty("status").GetString().ShouldBe("khong-hop-le");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message")).ShouldBe(1);
    }

    [Theory]
    [InlineData("loi-vinh-vien")]
    [InlineData("rang-buoc")]
    public async Task Loi_du_lieu_hoac_rang_buoc_cua_mot_ban_tin_khong_ket_ca_lo(string session)
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt12" + session[..3]);
        await env.Exec(FailTrigger);
        var bad = Guid.NewGuid();
        var ok = Guid.NewGuid();

        var response = await env.PostAgentAsync(null, env.AgentMessage(bad, session: session), env.AgentMessage(ok, seq: 2));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(response);
        Result(body).GetProperty("status").GetString().ShouldBe("khong-hop-le");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @bad", new { bad })).ShouldBe(0);
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @ok", new { ok })).ShouldBe(1);
    }

    [Fact]
    public async Task Loi_tam_khi_luu_503_cho_ca_lo_gui_lai_duoc()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt13");
        await env.Exec(FailTrigger);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var response = await env.PostAgentAsync(null, env.AgentMessage(first), env.AgentMessage(second, seq: 2, session: "loi-tam"));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // Hết lỗi tạm: gửi lại cả lô → bản tin đã lưu lần trước báo trùng, bản tin kia được lưu.
        await env.Exec("DROP TRIGGER raw_message_thu_trg ON raw_message");
        var again = await env.PostAgentAsync(null, env.AgentMessage(first), env.AgentMessage(second, seq: 2, session: "loi-tam"));
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(again);
        Result(body).GetProperty("status").GetString().ShouldBe("trung");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
    }

    [Fact]
    public async Task Trung_id_khac_may_cua_cung_agent_khong_hop_le_khong_lo_trang_thai()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "bt14");
        var secondInstrument = await env.Tenant.Instruments.SaveAsync(
            new Lis.Data.Instruments.InstrumentInfo(Guid.Empty, "Máy thứ hai", "BC-5380", "hl7-mllp", null, null, "XN-01", true));
        var id = Guid.NewGuid();
        (await env.PostAgentAsync(null, env.AgentMessage(id))).EnsureSuccessStatusCode();

        var response = await env.PostAgentAsync(null, env.AgentMessage(id, instrumentId: secondInstrument));

        var result = Result(await RawEnv.JsonOf(response));
        result.GetProperty("status").GetString().ShouldBe("khong-hop-le");
        result.GetProperty("decodeStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        (await env.Scalar<Guid>("SELECT instrument_id FROM raw_message WHERE id = @id", new { id })).ShouldBe(env.InstrumentId);
    }
}
