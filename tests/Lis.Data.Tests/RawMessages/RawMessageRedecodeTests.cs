using System.Text;
using Dapper;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.RawMessages;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.RawMessages;

/// <summary>
/// Giải mã lại bản tin thô không bao giờ ghi đè hay xoá âm thầm một kết quả đã có (vòng sửa 1, mục C1 và I3):
/// khác thì vào "chờ thay", giống thì bỏ qua, trùng bản chờ thay của bản tin khác thì không ghi; dòng QC / Chưa ghép
/// do bản tin này sinh ra từ lượt trước được thay bằng kết quả của bộ giải mã hiện tại.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RawMessageRedecodeTests(PostgresFixture fixture)
{
    private const string Redo = "giai-ma-lai";
    private const string Auto = "tu-dong";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Prop(RawMessageRow row, string name) => row.DecodeResult!.Value.GetProperty(name).GetInt32();

    private static async Task ExecAsync(RawEnv env, string sql, object? args = null)
    {
        await using var connection = await env.Db.OpenAsync(Ct);
        await connection.ExecuteAsync(sql, args);
    }

    /// <summary>Lưu bản tin với thứ tự truyền (completed_at, seq) tăng dần theo <paramref name="order"/>.</summary>
    private static async Task<Guid> StoreOrderedAsync(RawEnv env, byte[] bytes, int order)
    {
        var insert = env.Insert(bytes) with { Seq = order, CompletedAt = RawEnv.CompletedAt.AddMinutes(order) };
        (await env.Store.InsertIfAbsentAsync(insert, Ct)).ShouldNotBeNull();
        return insert.Id;
    }

    private static async Task<DateTimeOffset?> ApprovedAtAsync(RawEnv env, Guid sampleId) => (await env.Base.Results.GetAsync(sampleId)).ApprovedAt;

    [Fact]
    public async Task Giai_ma_lai_mau_chua_duyet_da_sua_tay_khong_de_ma_vao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Base.Results.SetManualAsync(items[env.WbcId], "5.00", env.Base.ActorId, "Sửa thử");

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("5.00");
        wbc.Source.ShouldBe("tay");
        wbc.PendingValue.ShouldBe("9.81");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m1}");
        Prop(row, "pendingReplacement").ShouldBe(1);
        Prop(row, "unchanged").ShouldBe(1);
        Prop(row, "stored").ShouldBe(0);
        (await ApprovedAtAsync(env, sampleId)).ShouldBeNull();
    }

    [Fact]
    public async Task Giai_ma_lai_khong_tu_duyet_mau()
    {
        var env = await RawEnv.CreateAsync(fixture);
        await ExecAsync(env, "UPDATE service SET auto_approve = true WHERE id IN (@a, @b)", new { a = env.WbcId, b = env.HgbId });
        var (sampleId, barcode, _) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7Unflagged((barcode, "5.10", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        (await ApprovedAtAsync(env, sampleId)).ShouldNotBeNull("lượt tự động vẫn tự duyệt");
        await env.Base.Results.UnapproveAsync(sampleId, env.Base.ActorId, "Mở lại thử");

        await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        (await ApprovedAtAsync(env, sampleId)).ShouldBeNull("lượt giải mã lại không tự duyệt");
    }

    [Fact]
    public async Task Giai_ma_lai_ban_tin_cu_khong_de_ket_qua_lan_chay_lai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        (await StoredAsync(env.Db, items[env.WbcId])).Value.ShouldBe("7.00");
        var historyBefore = (await HistoryAsync(env.Db, items[env.WbcId])).Count;

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        // Bản tin cũ hơn không tạo chờ thay đè lên kết quả của bản tin mới hơn.
        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("7.00");
        wbc.RawRef.ShouldBe($"ban-tin:{m2}");
        wbc.PendingValue.ShouldBeNull();
        (await HistoryAsync(env.Db, items[env.WbcId])).Count.ShouldBe(historyBefore);
        Prop(row, "olderThanCurrent").ShouldBe(1);
        Prop(row, "pendingReplacement").ShouldBe(0);
        Prop(row, "stored").ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_ban_tin_cu_nhung_ket_qua_moi_hon_da_sua_tay_van_vao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        await env.Base.Results.SetManualAsync(items[env.WbcId], "6.00", env.Base.ActorId, "Sửa thử");

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("6.00");
        wbc.Source.ShouldBe("tay");
        wbc.PendingValue.ShouldBe("5.00");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m1}");
        Prop(row, "pendingReplacement").ShouldBe(1);
        Prop(row, "olderThanCurrent").ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_ket_qua_tu_duong_cu_khong_phai_ban_tin_vao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await ExecAsync(env, "UPDATE result SET raw_ref = 'agent:cu-1' WHERE order_item_id = @id", new { id = items[env.WbcId] });
        await ExecAsync(env, "UPDATE result SET value = '7.00' WHERE order_item_id = @id", new { id = items[env.WbcId] });

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        (await StoredAsync(env.Db, items[env.WbcId])).PendingValue.ShouldBe("5.00");
        Prop(row, "pendingReplacement").ShouldBe(1);
        Prop(row, "olderThanCurrent").ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_khong_xoa_cho_thay_cua_ban_tin_moi_hon()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "5.00", "132")));
        var m2 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "7.00", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);
        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        (await StoredAsync(env.Db, items[env.WbcId])).PendingValue.ShouldBe("7.00");
        var historyBefore = (await HistoryAsync(env.Db, items[env.WbcId])).Count;

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("5.00");
        wbc.Status.ShouldBe("da-duyet");
        wbc.PendingValue.ShouldBe("7.00");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m2}");
        (await HistoryAsync(env.Db, items[env.WbcId])).Count.ShouldBe(historyBefore);
        Prop(row, "unchanged").ShouldBe(2);
        Prop(row, "pendingCleared").ShouldBe(0);
        (await ApprovedAtAsync(env, sampleId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Giai_ma_lai_khong_ghi_de_cho_thay_cua_ban_tin_khac_ghi_conflict()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        await env.Base.Results.SetManualAsync(items[env.WbcId], "5.00", env.Base.ActorId, null);
        await env.Base.Results.SetManualAsync(items[env.HgbId], "120", env.Base.ActorId, null);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "6.00", "120")));
        var m2 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "7.00", "120")));
        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        (await StoredAsync(env.Db, items[env.WbcId])).PendingValue.ShouldBe("7.00");

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("5.00");
        wbc.PendingValue.ShouldBe("7.00");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m2}");
        Prop(row, "conflicts").ShouldBe(1);
        Prop(row, "pendingReplacement").ShouldBe(0);
        Prop(row, "unchanged").ShouldBe(1);
        var history = await HistoryAsync(env.Db, items[env.WbcId]);
        history.ShouldContain(h => h.Action == "giai-ma-lai-khac" && h.Reason == "Giải mã lại ra giá trị khác, đang có giá trị chờ thay từ bản tin khác — không ghi" && h.NewValue == "6.00");
    }

    [Fact]
    public async Task Giai_ma_lai_cung_gia_tri_khac_co_may_thi_vao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await ExecAsync(env, "UPDATE result SET instrument_flag = '*' WHERE order_item_id = @id", new { id = items[env.WbcId] });

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("9.81");
        wbc.InstrumentFlag.ShouldBe("*");
        wbc.PendingValue.ShouldBe("9.81");
        Prop(row, "pendingReplacement").ShouldBe(1);
        Prop(row, "unchanged").ShouldBe(1);
    }

    [Fact]
    public async Task Giai_ma_lai_kenh_moi_chua_co_ket_qua_thi_ghi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        await ExecAsync(env, "DELETE FROM instrument_channel WHERE channel_code = '718-7'");
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        (await StoredOrNullAsync(env.Db, items[env.HgbId])).ShouldBeNull();
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @r", new { r = $"ban-tin:{m1}" })).ShouldBe(1);

        // Bộ giải mã / map đã sửa: nay đọc thêm được kênh HGB.
        await new InstrumentStore(env.Db).SaveChannelAsync(new ChannelMap(Guid.Empty, env.InstrumentId, "718-7", env.HgbId, null, null, false, false));
        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        var hgb = await StoredAsync(env.Db, items[env.HgbId]);
        hgb.Value.ShouldBe("132");
        hgb.PendingValue.ShouldBeNull();
        Prop(row, "stored").ShouldBe(1);
        Prop(row, "unchanged").ShouldBe(1);
        Prop(row, "supersededUnmatched").ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @r", new { r = $"ban-tin:{m1}" })).ShouldBe(0);
        (await ApprovedAtAsync(env, sampleId)).ShouldBeNull();
    }

    [Fact]
    public async Task Giai_ma_lai_thay_dong_chua_ghep_sai_bang_dong_dung()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0009", "7.00", "100")));
        var rawRef = $"ban-tin:{id}";
        // Bộ giải mã cũ đọc sai giá trị: dòng sai do chính bản tin này sinh ra, chưa ai xử lý.
        await ExecAsync(env,
            """
            INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, reason, raw_ref)
            VALUES (@i, 'KHONGCO0009', '6690-2', '99.99', 'khong-co-barcode', @rawRef)
            """,
            new { i = env.InstrumentId, rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        Prop(row, "supersededUnmatched").ShouldBe(1);
        Prop(row, "unmatched").ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND value = '99.99'", new { rawRef })).ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_thay_dong_qc_sai_bang_dong_dung()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(true, ("QC-THU-02", "6.10", "140")));
        var rawRef = $"ban-tin:{id}";
        await ExecAsync(env,
            "INSERT INTO qc_result (instrument_id, barcode, channel_code, value, raw_ref) VALUES (@i, 'QC-THU-02', '6690-2', '99.99', @rawRef)",
            new { i = env.InstrumentId, rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        Prop(row, "supersededQc").ShouldBe(1);
        Prop(row, "qc").ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM qc_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM qc_result WHERE raw_ref = @rawRef AND value = '99.99'", new { rawRef })).ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_giu_dong_chua_ghep_da_xu_ly()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0010", "7.00", "100")));
        var rawRef = $"ban-tin:{id}";
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        await ExecAsync(env,
            "UPDATE unmatched_result SET resolved_at = now(), resolved_by = @actor, dismiss_reason = 'Bỏ qua thử' WHERE raw_ref = @rawRef AND channel_code = '6690-2'",
            new { actor = env.Base.ActorId, rawRef });
        await using var connection = await env.Db.OpenAsync(Ct);
        var resolvedId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM unmatched_result WHERE raw_ref = @rawRef AND resolved_at IS NOT NULL", new { rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        // Dòng đã xử lý giữ nguyên và không tạo lại; dòng chưa xử lý được thay bằng bản mới.
        Prop(row, "supersededUnmatched").ShouldBe(1);
        Prop(row, "unmatched").ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(2);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM unmatched_result WHERE id = @resolvedId AND resolved_at IS NOT NULL", new { resolvedId })).ShouldBe(1);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2'", new { rawRef })).ShouldBe(1);
    }

    [Fact]
    public async Task Giai_ma_lai_ket_qua_sua_tay_co_trong_hl7_co_N_cung_gia_tri_la_khong_doi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Base.Results.SetManualAsync(items[env.WbcId], "9.81", env.Base.ActorId, "Nhập tay");
        (await StoredAsync(env.Db, items[env.WbcId])).InstrumentFlag.ShouldBeNull();

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        // Cờ trống (tay) và cờ "N" (HL7 báo bình thường) là một: không sinh chờ thay giả.
        (await StoredAsync(env.Db, items[env.WbcId])).PendingValue.ShouldBeNull();
        Prop(row, "unchanged").ShouldBe(2);
        Prop(row, "pendingReplacement").ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_lan_hai_khong_ghi_trung_cho_thay_cua_chinh_ban_tin()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Base.Results.SetManualAsync(items[env.WbcId], "5.00", env.Base.ActorId, "Sửa thử");
        await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);
        // Cờ chờ thay lưu khác dạng ("n ~ N" vẫn là bình thường) nhưng cùng giá trị: giải mã lại lần nữa không ghi thêm.
        await ExecAsync(env, "UPDATE result SET pending_instrument_flag = 'n~N' WHERE order_item_id = @id", new { id = items[env.WbcId] });
        var historyBefore = (await HistoryAsync(env.Db, items[env.WbcId])).Count;

        var row = await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);

        Prop(row, "pendingReplacement").ShouldBe(0);
        Prop(row, "unchanged").ShouldBe(2);
        (await HistoryAsync(env.Db, items[env.WbcId])).Count.ShouldBe(historyBefore);
    }

    [Fact]
    public void NormalFlag_bo_N_va_rong_sap_xep_va_noi()
    {
        InstrumentValuePipeline.NormalFlag(null).ShouldBeNull();
        InstrumentValuePipeline.NormalFlag("").ShouldBeNull();
        InstrumentValuePipeline.NormalFlag("  ").ShouldBeNull();
        InstrumentValuePipeline.NormalFlag("N").ShouldBeNull();
        InstrumentValuePipeline.NormalFlag(" n ").ShouldBeNull();
        InstrumentValuePipeline.NormalFlag("N~N;n").ShouldBeNull();
        InstrumentValuePipeline.NormalFlag("H").ShouldBe("H");
        InstrumentValuePipeline.NormalFlag("N~H").ShouldBe("H");
        InstrumentValuePipeline.NormalFlag("L;H").ShouldBe(InstrumentValuePipeline.NormalFlag("H~L"));
        InstrumentValuePipeline.NormalFlag("L;H").ShouldBe("H~L");
    }

    [Fact]
    public async Task Giai_ma_lai_ra_0_ket_qua_giu_dong_chua_ghep_va_qc()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync([0x0D]);
        var rawRef = $"ban-tin:{id}";
        await ExecAsync(env,
            """
            INSERT INTO unmatched_result (instrument_id, barcode, channel_code, value, reason, raw_ref)
            VALUES (@i, 'KHONGCO0030', '6690-2', '7.00', 'khong-co-barcode', @rawRef)
            """,
            new { i = env.InstrumentId, rawRef });
        await ExecAsync(env,
            "INSERT INTO qc_result (instrument_id, barcode, channel_code, value, raw_ref) VALUES (@i, 'QC-THU-03', '6690-2', '6.10', @rawRef)",
            new { i = env.InstrumentId, rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Error);
        row.DecodeResult!.Value.GetProperty("error").GetString()
            .ShouldBe("Giải mã lại không ra kết quả nào — giữ nguyên dòng Chưa ghép/QC cũ. Kiểm cấu hình máy.");
        Prop(row, "supersededUnmatched").ShouldBe(0);
        Prop(row, "supersededQc").ShouldBe(0);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM qc_result WHERE raw_ref = @rawRef", new { rawRef })).ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'giai-ma-lai'", new { id })).ShouldBe(1);
    }

    [Fact]
    public async Task Tu_hoc_map_kenh_chi_chay_sau_luot_tu_dong()
    {
        var learning = new CountingLearning();
        var env = await RawEnv.CreateAsync(fixture, learning: learning);
        var (_, barcode, _) = await env.NewSampleAsync();
        await ExecAsync(env, "DELETE FROM instrument_channel WHERE channel_code = '718-7'");
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        learning.Calls.ShouldBe(1);

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        Prop(row, "unmatched").ShouldBe(1);
        learning.Calls.ShouldBe(1, "lượt giải mã lại có kenh-chua-map cũng không tự học");
    }

    [Fact]
    public async Task Giai_ma_lai_dong_chua_ghep_da_xu_ly_khac_gia_tri_vao_resolvedMismatch()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, _, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0031", "7.00", "100")));
        var rawRef = $"ban-tin:{id}";
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        // Dòng "đã gán" (resolved_item_id có giá trị), khác với dòng chỉ bỏ qua.
        await ExecAsync(env,
            "UPDATE unmatched_result SET value = '99.99', resolved_at = now(), resolved_by = @actor, resolved_item_id = @item WHERE raw_ref = @rawRef AND channel_code = '6690-2'",
            new { actor = env.Base.ActorId, rawRef, item = items[env.WbcId] });
        await using var connection = await env.Db.OpenAsync(Ct);
        var oldId = await connection.ExecuteScalarAsync<Guid>("SELECT id FROM unmatched_result WHERE value = '99.99' AND raw_ref = @rawRef", new { rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        var mismatch = row.DecodeResult!.Value.GetProperty("resolvedMismatch");
        mismatch.GetArrayLength().ShouldBe(1);
        mismatch[0].GetProperty("unmatchedId").GetGuid().ShouldBe(oldId);
        mismatch[0].GetProperty("barcode").GetString().ShouldBe("KHONGCO0031");
        mismatch[0].GetProperty("channel").GetString().ShouldBe("6690-2");
        mismatch[0].GetProperty("oldValue").GetString().ShouldBe("99.99");
        mismatch[0].GetProperty("newValue").GetString().ShouldBe("7.00");
        row.DecodeResult.Value.GetProperty("warnings").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        // Không tạo dòng Chưa ghép mới trùng với dòng đã gán (kênh HGB vẫn có dòng mới).
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2'", new { rawRef })).ShouldBe(1);
    }

    [Fact]
    public async Task Giai_ma_lai_ban_tin_chi_co_cau_hoi_ra_khong_co_ket_qua()
    {
        var env = await RawEnv.CreateAsync(fixture, family: "astm-e1394");
        byte[] bytes = [0x05, .. AstmFrame(1, "H|\\^&|||Máy Thử\rQ|1|^2611030001||ALL||||||||O\rL|1|N\r"), 0x04];
        var id = await env.StoreAsync(bytes);
        (await env.Processor.DecodeAsync(id, Auto, null, Ct)).DecodeStatus.ShouldBe(RawMessageStatus.NoResult);

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.NoResult);
        row.DecodeResult!.Value.GetProperty("error").GetString().ShouldBeNull();
    }

    [Fact]
    public async Task Giai_ma_lai_ban_tin_chi_CR_khong_co_dong_cu_ra_khong_co_ket_qua()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync([0x0D]);
        (await env.Processor.DecodeAsync(id, Auto, null, Ct)).DecodeStatus.ShouldBe(RawMessageStatus.NoResult);

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.NoResult);
    }

    [Fact]
    public async Task Giai_ma_lai_ra_0_ket_qua_khi_luot_truoc_da_giai_ma_thi_van_la_loi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);
        await ExecAsync(env, "UPDATE raw_message SET bytes = @b WHERE id = @id", new { id, b = new byte[] { 0x0D } });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Error);
    }

    [Fact]
    public async Task Giai_ma_lai_tao_cho_thay_roi_may_gui_moi_thi_bo_cho_thay_cu()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);
        await env.Processor.DecodeAsync(m1, Auto, null, Ct);
        await env.Base.Results.SetManualAsync(items[env.WbcId], "6.00", env.Base.ActorId, "Sửa thử");
        await env.Processor.DecodeAsync(m1, Redo, env.Base.ActorId, Ct);
        (await StoredAsync(env.Db, items[env.WbcId])).PendingValue.ShouldBe("5.00");

        await env.Processor.DecodeAsync(m2, Auto, null, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("7.00");
        wbc.RawRef.ShouldBe($"ban-tin:{m2}");
        wbc.PendingValue.ShouldBeNull();
        wbc.PendingRawRef.ShouldBeNull();
        var dropped = (await HistoryAsync(env.Db, items[env.WbcId])).Where(h => h.Action == "bo-thay-the").ToList();
        dropped.Count.ShouldBe(1);
        dropped[0].OldValue.ShouldBe("5.00");
        dropped[0].Reason.ShouldBe("Máy gửi kết quả mới, bỏ giá trị chờ thay cũ");
    }

    private static async Task<Guid> TwoSameChannelMessageAsync(RawEnv env, string barcode)
    {
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "7.00", "100"), (barcode, "8.00", "100")));
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        return id;
    }

    [Fact]
    public async Task Giai_ma_lai_hai_item_cung_barcode_kenh_chi_tieu_mot_dong_da_gan()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, _, items) = await env.NewSampleAsync();
        var id = await TwoSameChannelMessageAsync(env, "KHONGCO0040");
        var rawRef = $"ban-tin:{id}";
        await ExecAsync(env,
            "UPDATE unmatched_result SET resolved_at = now(), resolved_by = @actor, resolved_item_id = @item WHERE raw_ref = @rawRef AND channel_code = '6690-2' AND value = '7.00'",
            new { actor = env.Base.ActorId, item = items[env.WbcId], rawRef });

        await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2'", new { rawRef })).ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2' AND resolved_at IS NULL", new { rawRef })).ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2' AND resolved_at IS NULL AND value = '8.00'", new { rawRef })).ShouldBe(1);
    }

    [Fact]
    public async Task Giai_ma_lai_dong_bo_qua_khac_gia_tri_tao_dong_chua_ghep_moi_khong_bao_lech()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0041", "7.00", "100")));
        var rawRef = $"ban-tin:{id}";
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        await ExecAsync(env,
            "UPDATE unmatched_result SET value = '99.99', resolved_at = now(), resolved_by = @actor, dismiss_reason = 'Thử' WHERE raw_ref = @rawRef AND channel_code = '6690-2'",
            new { actor = env.Base.ActorId, rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2' AND resolved_at IS NULL AND value = '7.00'", new { rawRef })).ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2' AND resolved_at IS NOT NULL", new { rawRef })).ShouldBe(1);
        row.DecodeResult!.Value.GetProperty("resolvedMismatch").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Giai_ma_lai_dong_bo_qua_cung_gia_tri_thi_khong_tao_lai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0042", "7.00", "100")));
        var rawRef = $"ban-tin:{id}";
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        await ExecAsync(env,
            "UPDATE unmatched_result SET value = ' 7.00 ', resolved_at = now(), resolved_by = @actor, dismiss_reason = 'Thử' WHERE raw_ref = @rawRef AND channel_code = '6690-2'",
            new { actor = env.Base.ActorId, rawRef });

        var row = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        (await env.CountAsync("SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef AND channel_code = '6690-2'", new { rawRef })).ShouldBe(1);
        row.DecodeResult!.Value.GetProperty("resolvedMismatch").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Token_da_huy_ma_loi_khong_phai_huy_thi_xu_ly_nhu_tat_may_khong_ghi_loi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        env.Processor.AfterReadingIngested = _ =>
        {
            shutdown.Cancel();
            throw new InvalidOperationException("Kết nối đứt vì tắt máy");
        };

        await Should.ThrowAsync<OperationCanceledException>(() => env.Processor.DecodeAsync(id, Auto, null, shutdown.Token));

        var row = (await env.Store.FindAsync(id))!;
        row.DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id })).ShouldBe(0);
    }

    [Fact]
    public async Task Hoan_tat_luot_dat_lai_so_lan_claim_ve_0()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        await env.Processor.DecodeAsync(id, Auto, null, Ct);

        (await env.CountAsync("SELECT decode_attempts FROM raw_message WHERE id = @id", new { id })).ShouldBe(0);
    }

    [Fact]
    public async Task Recover_ghi_log_so_dong_chuyen_trang_thai()
    {
        var logs = new List<string>();
        var env = await RawEnv.CreateAsync(fixture);
        var processor = new RawMessageProcessor(env.Db, env.Ingest, null, env.Clock, logs.Add);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0032", "9.81", "132")));
        await processor.ClaimAsync(id, "tu-dong", Ct);
        env.Clock.Advance(RawMessageStore.DefaultRecoverGrace + TimeSpan.FromSeconds(1));

        var recovery = await processor.RecoverAsync(Ct);

        recovery.Requeued.ShouldBe(1);
        recovery.NeedsReview.ShouldBe(0);
        logs.ShouldContain(l => l.Contains("1 bản tin về cho-giai-ma") && l.Contains("0 bản tin sang can-xem-lai"));
    }

    private sealed class CountingLearning : Lis.Data.Ingest.IChannelMapLearning
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Lis.Data.Ingest.ChannelMapHint>> SuggestAsync(Guid instrumentId) =>
            Task.FromResult<IReadOnlyList<Lis.Data.Ingest.ChannelMapHint>>([]);

        public Task<int> ConfirmAsync(Guid instrumentId, string channelCode, Guid serviceId, Guid? actorId) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> AutoLearnAsync(Guid instrumentId)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    [Fact]
    public async Task Ban_tin_chi_co_byte_roi_la_khong_co_ket_qua_khong_phai_loi()
    {
        var hl7 = await RawEnv.CreateAsync(fixture);
        foreach (var (bytes, partial) in new (byte[], bool)[]
                 {
                     ([0x0D], false), ([0x0B, 0x1C, 0x0D], false), ([0x20, 0x0A, 0x20], true), ([0x0D, 0x0A], true),
                 })
        {
            var id = await hl7.StoreAsync(bytes, partial);
            var row = await hl7.Processor.DecodeAsync(id, Auto, null, Ct);
            row.DecodeStatus.ShouldBe(RawMessageStatus.NoResult, $"byte {Convert.ToHexString(bytes)} partial={partial}");
        }

        var astm = await RawEnv.CreateAsync(fixture, family: "astm-e1394");
        foreach (var bytes in new byte[][] { [0x05], [0x04], [0x05, 0x04] })
        {
            var id = await astm.StoreAsync(bytes, partial: true);
            var row = await astm.Processor.DecodeAsync(id, Auto, null, Ct);
            row.DecodeStatus.ShouldBe(RawMessageStatus.NoResult, $"ASTM byte {Convert.ToHexString(bytes)}");
        }
    }

    [Fact]
    public async Task Ban_tin_co_ket_qua_nhung_driver_bo_du_lieu_la_da_giai_ma_kem_canh_bao()
    {
        var env = await RawEnv.CreateAsync(fixture, family: "astm-e1394");
        var text = "H|\\^&|||Máy Thử\rP|1\rO|1|KHONGCO0011||^^^WBC\rR|1|^^^WBC|9.81|G/L\rL|1|N\r";
        var good = AstmFrame(1, text);
        var bad = AstmFrame(1, text, badChecksum: true);
        // ENQ, khung sai checksum (bị bỏ, máy gửi lại), khung đúng, EOT.
        byte[] bytes = [0x05, .. bad, .. good, 0x04];
        var id = await env.StoreAsync(bytes);

        var row = await env.Processor.DecodeAsync(id, Auto, null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        row.DecodeResult!.Value.GetProperty("readings").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        row.DecodeResult.Value.GetProperty("warnings").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    private static byte[] AstmFrame(int number, string text, bool badChecksum = false)
    {
        var body = Encoding.UTF8.GetBytes($"{number}{text}");
        var sum = (body.Sum(b => b) + 0x03) % 256;
        var checksum = badChecksum ? "00" : sum.ToString("X2");
        return [0x02, .. body, 0x03, .. Encoding.ASCII.GetBytes(checksum), 0x0D, 0x0A];
    }
}
