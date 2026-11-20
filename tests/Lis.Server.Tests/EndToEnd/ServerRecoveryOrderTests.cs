using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests.EndToEnd;

/// <summary>
/// Trọn vòng thật (HTTP + CSDL + worker phục hồi): server chết sau khi lưu bản tin, trước khi giải mã — bản tin cũ không được
/// đè giá trị của bản tin mới hơn, dù chạy lại bằng endpoint hay bằng vòng phục hồi (I1).
/// </summary>
public sealed class ServerRecoveryOrderTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Result(JsonElement body, int index = 0) => body.GetProperty("results")[index];

    [Fact]
    public async Task Server_chet_sau_khi_luu_M1_M2_cung_mau_duoc_giai_ma_roi_phuc_hoi_M1_khong_de_M2()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "rec1");
        // M1 (cũ): đã lưu, server chết trước khi giải mã → còn cho-giai-ma.
        var m1 = await env.StoreAsync(RawEnv.Hl7(env.Barcode, wbc: "5.00"), RawEnv.CompletedAt.AddSeconds(1), seq: 1);
        // M2 (mới hơn, cùng mẫu, giá trị khác) vào lúc server đã bật lại, giải mã ngay.
        var post = await env.PostAgentAsync(null, env.AgentMessage(Guid.NewGuid(), RawEnv.Hl7(env.Barcode, wbc: "7.00"), seq: 2));
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("7.00");
        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @m1", new { m1 })).ShouldBe("cho-giai-ma");

        await env.Host.RawWorker.RecoverAllAsync(Ct);

        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("7.00", "bản tin cũ chạy sau không đè giá trị của bản tin mới");
        (await env.Scalar<string>("SELECT pending_value FROM result WHERE order_item_id = @id", new { id = env.WbcItemId })).ShouldBe("5.00");
        (await env.Scalar<string>("SELECT pending_reason FROM result WHERE order_item_id = @id", new { id = env.WbcItemId }))
            .ShouldBe("Bản tin cũ đến sau — kết quả hiện tại từ bản tin mới hơn");
        (await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @m1", new { m1 })).ShouldBe("da-giai-ma");
        (await env.Scalar<int>("SELECT (decode_result->>'olderThanCurrent')::int FROM raw_message WHERE id = @m1", new { m1 })).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Agent_gui_lai_lo_M1_trung_con_cho_giai_ma_thi_giai_ma_ngay_dung_thu_tu()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "rec2");
        var m1 = await env.StoreAsync(RawEnv.Hl7(env.Barcode, wbc: "5.00"), RawEnv.CompletedAt.AddSeconds(1), seq: 1);
        var m2 = Guid.NewGuid();

        var post = await env.PostAgentAsync(null,
            env.AgentMessage(m1, RawEnv.Hl7(env.Barcode, wbc: "5.00"), seq: 1),
            env.AgentMessage(m2, RawEnv.Hl7(env.Barcode, wbc: "7.00"), seq: 2));

        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await RawEnv.JsonOf(post);
        Result(body).GetProperty("status").GetString().ShouldBe("trung");
        Result(body).GetProperty("decodeStatus").GetString().ShouldBe("da-giai-ma");
        Result(body, 1).GetProperty("status").GetString().ShouldBe("da-luu");
        // M1 giải mã trước, M2 sau: giá trị cuối là của M2, không cần worker.
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("7.00");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode WHERE raw_message_id = @m1", new { m1 })).ShouldBe(1);
    }

    [Fact]
    public async Task Agent_gui_lai_M1_trung_dang_giai_ma_thi_bo_qua_khong_giai_ma()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "rec3");
        var m1 = await env.StoreAsync(RawEnv.Hl7(env.Barcode, wbc: "5.00"), RawEnv.CompletedAt.AddSeconds(1), seq: 1);
        await env.Exec(
            "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = now(), decode_kind = 'tu-dong', decode_attempts = 1 WHERE id = @m1",
            new { m1 });

        var post = await env.PostAgentAsync(null, env.AgentMessage(m1, RawEnv.Hl7(env.Barcode, wbc: "5.00"), seq: 1));

        var body = await RawEnv.JsonOf(post);
        Result(body).GetProperty("status").GetString().ShouldBe("trung");
        Result(body).GetProperty("decodeStatus").GetString().ShouldBe("dang-giai-ma");
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBeNull();
    }

    [Fact]
    public async Task Mau_chua_duyet_chay_lai_gia_tri_khac_qua_nhieu_lo_gia_tri_cuoi_la_cua_ban_tin_moi_nhat()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "rec4");
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var values = new[] { "5.00", "7.00", "9.00" };

        for (var i = 0; i < ids.Length; i++)
        {
            var post = await env.PostAgentAsync(null, env.AgentMessage(ids[i], RawEnv.Hl7(env.Barcode, wbc: values[i]), seq: i + 1));
            post.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await env.ValueOfAsync(env.WbcItemId)).ShouldBe(values[i]);
        }

        // Agent gửi lại lô đầu (mất ACK): trùng, không đè lại.
        var again = await env.PostAgentAsync(null, env.AgentMessage(ids[0], RawEnv.Hl7(env.Barcode, wbc: values[0]), seq: 1));
        Result(await RawEnv.JsonOf(again)).GetProperty("status").GetString().ShouldBe("trung");
        await env.Host.RawWorker.RecoverAllAsync(Ct);

        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("9.00");
        (await env.Scalar<int>("SELECT count(*)::int FROM raw_message_decode")).ShouldBe(3);
    }

    [Fact]
    public async Task Giai_ma_lai_tron_vong_sau_khi_sua_map_mau_da_duyet_va_chua_ghep_da_gan()
    {
        await using var env = await RawEnv.CreateAsync(fixture, "rec5");
        var actor = await env.UserIdAsync("ktv");
        var hgbServiceId = await env.Scalar<Guid>("SELECT service_id FROM order_item WHERE id = @id", new { id = env.HgbItemId });
        var sampleId = await env.Scalar<Guid>("SELECT sample_id FROM order_item WHERE id = @id", new { id = env.HgbItemId });
        // Kênh HGB chưa map: lượt tự động ra WBC + một dòng Chưa ghép.
        await env.Exec("DELETE FROM instrument_channel WHERE channel_code = '718-7'");
        var m1 = Guid.NewGuid();
        (await env.PostAgentAsync(null, env.AgentMessage(m1, RawEnv.Hl7(env.Barcode, wbc: "9.81", hgb: "132"), seq: 1))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("9.81");
        var rawRef = $"ban-tin:{m1}";
        var unmatchedId = await env.Scalar<Guid>("SELECT id FROM unmatched_result WHERE raw_ref = @rawRef", new { rawRef });

        // Người dùng gán dòng Chưa ghép vào HGB, rồi duyệt mẫu.
        await new Lis.Data.Ingest.UnmatchedService(env.Db, TimeProvider.System).AssignAsync(unmatchedId, env.HgbItemId, actor);
        (await env.ValueOfAsync(env.HgbItemId)).ShouldBe("132");
        await env.Tenant.Results.ApproveAsync(sampleId, actor);

        // Sửa map rồi giải mã lại (bộ giải mã nay đọc ra 140 thay vì 132).
        await env.Tenant.Instruments.SaveChannelAsync(new Lis.Data.Instruments.ChannelMap(Guid.Empty, env.InstrumentId, "718-7", hgbServiceId, null, null, false, false));
        await env.Exec("UPDATE raw_message SET bytes = @bytes WHERE id = @m1", new { m1, bytes = RawEnv.Hl7(env.Barcode, wbc: "9.81", hgb: "140") });
        var row = await env.Tenant.RawProcessor.DecodeAsync(m1, "giai-ma-lai", actor, Ct);

        row.DecodeStatus.ShouldBe("da-giai-ma");
        (await env.ValueOfAsync(env.HgbItemId)).ShouldBe("132", "mẫu đã duyệt: không đè");
        (await env.Scalar<string>("SELECT pending_value FROM result WHERE order_item_id = @id", new { id = env.HgbItemId })).ShouldBe("140");
        (await env.Scalar<string>("SELECT pending_reason FROM result WHERE order_item_id = @id", new { id = env.HgbItemId })).ShouldBe("Giải mã lại ra giá trị khác");
        (await env.ValueOfAsync(env.WbcItemId)).ShouldBe("9.81");
        (await env.Scalar<bool>("SELECT approved_at IS NOT NULL FROM sample WHERE id = @sampleId", new { sampleId })).ShouldBeTrue();
        // Dòng Chưa ghép đã xử lý giữ nguyên, không sinh dòng mới.
        (await env.Scalar<int>("SELECT count(*)::int FROM unmatched_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(1);
        (await env.Scalar<int>("SELECT count(*)::int FROM unmatched_result WHERE raw_ref = @rawRef AND resolved_at IS NOT NULL", new { rawRef })).ShouldBe(1);
    }
}
