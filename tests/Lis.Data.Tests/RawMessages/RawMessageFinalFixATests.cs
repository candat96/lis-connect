using System.Text;
using System.Text.Json;
using Dapper;
using Lis.Core.Ingest;
using Lis.Core.Results;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.RawMessages;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.RawMessages;

/// <summary>
/// Vòng sửa cuối A: bản tin cũ đến sau không đè bản mới (I1), nguồn và giờ của bản chờ thay (I4), bản tin không nhận ra
/// báo lỗi (I5), giải mã lại ra ít dòng hơn (I7), vết tự map kênh (I9), hàng rào theo số lần claim (M5), NUL (M6).
/// </summary>
[Collection(PostgresCollection.Name)]
public class RawMessageFinalFixATests(PostgresFixture fixture)
{
    private const string Redo = "giai-ma-lai";
    private const string Auto = "tu-dong";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Prop(RawMessageRow row, string name) => row.DecodeResult!.Value.GetProperty(name).GetInt32();

    private static List<string> Strings(RawMessageRow row, string name) =>
        row.DecodeResult!.Value.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToList();

    private static async Task ExecAsync(RawEnv env, string sql, object? args = null)
    {
        await using var connection = await env.Db.OpenAsync(Ct);
        await connection.ExecuteAsync(sql, args);
    }

    private static async Task<Guid> StoreOrderedAsync(RawEnv env, byte[] bytes, int order)
    {
        var insert = env.Insert(bytes) with { Seq = order, CompletedAt = RawEnv.CompletedAt.AddMinutes(order) };
        (await env.Store.InsertIfAbsentAsync(insert, Ct)).ShouldNotBeNull();
        return insert.Id;
    }

    private sealed record PendingColumns
    {
        public string? PendingReason { get; init; }
        public DateTimeOffset? PendingAt { get; init; }
        public DateTimeOffset? PendingMeasuredAt { get; init; }
    }

    private static async Task<PendingColumns> PendingColumnsAsync(RawEnv env, Guid orderItemId)
    {
        await using var connection = await env.Db.OpenAsync(Ct);
        return await connection.QuerySingleAsync<PendingColumns>(
            "SELECT pending_reason AS PendingReason, pending_at AS PendingAt, pending_measured_at AS PendingMeasuredAt FROM result WHERE order_item_id = @orderItemId",
            new { orderItemId });
    }

    private async Task<(RawEnv Env, Guid SampleId, string Barcode, IReadOnlyDictionary<Guid, Guid> Items)> ApprovedSampleAsync()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        await env.Base.Results.SetManualAsync(items[env.WbcId], "5.00", env.Base.ActorId, null);
        await env.Base.Results.SetManualAsync(items[env.HgbId], "130", env.Base.ActorId, null);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);
        return (env, sampleId, barcode, items);
    }

    // ---------------------------------------------------------------- I1

    [Fact]
    public async Task Ban_tin_cu_den_sau_mau_chua_duyet_khong_de_ban_tin_moi_vao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);

        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        var row = await env.Processor.DecodeAsync(m1, Auto, null, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("7.00");
        wbc.RawRef.ShouldBe($"ban-tin:{m2}");
        wbc.PendingValue.ShouldBe("5.00");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m1}");
        var columns = await PendingColumnsAsync(env, items[env.WbcId]);
        columns.PendingReason.ShouldBe("Bản tin cũ đến sau — kết quả hiện tại từ bản tin mới hơn");
        columns.PendingMeasuredAt.ShouldBe(RawEnv.CompletedAt.AddMinutes(1));
        Prop(row, "olderThanCurrent").ShouldBeGreaterThanOrEqualTo(1);
        Prop(row, "stored").ShouldBe(0);
    }

    [Fact]
    public async Task Ban_tin_cu_den_sau_cung_gia_tri_khong_tao_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);

        await env.Processor.DecodeAsync(m2, Auto, null, Ct);
        var row = await env.Processor.DecodeAsync(m1, Auto, null, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("7.00");
        wbc.PendingValue.ShouldBeNull();
        Prop(row, "olderThanCurrent").ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Ban_tin_cu_den_sau_khong_de_cho_thay_cua_ban_tin_khac()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "5.00", "132")), 1);
        var m2 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "7.00", "132")), 2);
        var m3 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "9.00", "132")), 3);
        await env.Processor.DecodeAsync(m3, Auto, null, Ct);
        await env.Processor.DecodeAsync(m2, Auto, null, Ct);

        var row = await env.Processor.DecodeAsync(m1, Auto, null, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("9.00");
        wbc.PendingValue.ShouldBe("7.00");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{m2}");
        Prop(row, "conflicts").ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Mau_da_duyet_ban_tin_cu_hon_van_vao_cho_thay_nhu_cu()
    {
        var (env, _, barcode, items) = await ApprovedSampleAsync();
        var m1 = await StoreOrderedAsync(env, RawEnv.Hl7(false, (barcode, "9.81", "130")), 1);

        await env.Processor.DecodeAsync(m1, Auto, null, Ct);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("5.00");
        wbc.PendingValue.ShouldBe("9.81");
        (await PendingColumnsAsync(env, items[env.WbcId])).PendingReason.ShouldBe("Máy gửi lại kết quả cho mẫu đã duyệt");
    }

    // ---------------------------------------------------------------- I4

    [Fact]
    public async Task Cho_thay_gia_tri_may_nhieu_so_le_van_co_ly_do_va_gio_do()
    {
        var (env, sampleId, barcode, items) = await ApprovedSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "7.456", "130")));

        await env.Processor.DecodeAsync(id, Auto, null, Ct);

        var row = (await env.Base.Results.GetAsync(sampleId)).Rows.Single(r => r.ServiceCode == "WBC");
        row.PendingValue.ShouldBe("7.46");
        row.PendingReason.ShouldBe("Máy gửi lại kết quả cho mẫu đã duyệt");
        row.PendingAt.ShouldNotBeNull();
        row.PendingMeasuredAt.ShouldBe(RawEnv.CompletedAt);
        (await PendingColumnsAsync(env, items[env.HgbId])).PendingReason.ShouldBeNull("HGB gửi lại đúng giá trị, không có chờ thay");
    }

    [Fact]
    public async Task Cho_thay_chi_so_dinh_tinh_van_co_ly_do()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var hbsId = await SaveServiceAsync(env.Base.Catalog, "HBSAG", "HBsAg", env.Base.EdtaId, decimals: null, unit: null, valueType: "dinh-tinh");
        await env.Base.Catalog.SaveQualitativeRulesAsync(hbsId,
        [
            new QualitativeRule(null, hbsId, "neg", "Âm tính", null),
            new QualitativeRule(null, hbsId, "pos", "Dương tính", ResultFlag.Abnormal),
        ]);
        await new InstrumentStore(env.Db).SaveChannelAsync(new ChannelMap(Guid.Empty, env.InstrumentId, "HBS", hbsId, null, null, false, false));
        var (sampleId, items) = await CreateSampleAsync(env.Base, FemaleAdult(), hbsId);
        var barcode = await BarcodeAsync(env.Db, sampleId);
        await env.Base.Results.SetManualAsync(items[hbsId], "Âm tính", env.Base.ActorId, null);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);

        await env.Ingest.IngestAsync(new InstrumentReading(env.InstrumentId, barcode, false, RawEnv.CompletedAt, "MSG-DT", [new ReadingItem("HBS", "pos", null, null)]));

        var row = (await env.Base.Results.GetAsync(sampleId)).Rows.Single();
        row.PendingValue.ShouldBe("Dương tính");
        row.PendingReason.ShouldBe("Máy gửi lại kết quả cho mẫu đã duyệt");
        row.PendingMeasuredAt.ShouldBe(RawEnv.CompletedAt);
    }

    [Fact]
    public async Task Cho_thay_tu_gan_chua_ghep_co_ly_do_rieng()
    {
        var (env, sampleId, barcode, items) = await ApprovedSampleAsync();
        await ExecAsync(env, "DELETE FROM instrument_channel WHERE channel_code = '6690-2'");
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "7.456", "130")));
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        var unmatched = new UnmatchedService(env.Db, env.Clock);
        var open = (await unmatched.ListOpenAsync(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20))).Single();

        await unmatched.AssignAsync(open.Id, items[env.WbcId], env.Base.ActorId);

        var row = (await env.Base.Results.GetAsync(sampleId)).Rows.Single(r => r.ServiceCode == "WBC");
        row.PendingValue.ShouldNotBeNull();
        row.PendingReason.ShouldBe("Gán tay từ Chưa ghép");
        row.PendingMeasuredAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Chap_nhan_va_tu_choi_cho_thay_xoa_nguon_va_gio()
    {
        var (env, _, barcode, items) = await ApprovedSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "130")));
        await env.Processor.DecodeAsync(id, Auto, null, Ct);
        var stored = await StoredAsync(env.Db, items[env.WbcId]);
        (await PendingColumnsAsync(env, items[env.WbcId])).PendingAt.ShouldNotBeNull();

        await env.Base.Results.RejectPendingAsync(stored.Id, env.Base.ActorId);

        (await PendingColumnsAsync(env, items[env.WbcId])).ShouldBe(new PendingColumns());

        // Giải mã lại tạo chờ thay mới, rồi chấp nhận.
        await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);
        (await PendingColumnsAsync(env, items[env.WbcId])).PendingReason.ShouldBe("Giải mã lại ra giá trị khác");
        await env.Base.Results.AcceptPendingAsync(stored.Id, env.Base.ActorId);
        (await PendingColumnsAsync(env, items[env.WbcId])).ShouldBe(new PendingColumns());
    }

    // ---------------------------------------------------------------- I5

    [Fact]
    public async Task Ban_tin_dang_do_khong_nhan_ra_la_loi_con_khong_dang_do_la_khong_co_ket_qua()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var garbage = Encoding.ASCII.GetBytes("ÿþ rac rac khong phai HL7 \u0080\u0081");

        var partial = await env.Processor.DecodeAsync(await env.StoreAsync(garbage, partial: true), Auto, null, Ct);
        var complete = await env.Processor.DecodeAsync(await env.StoreAsync(garbage, partial: false), Auto, null, Ct);

        partial.DecodeStatus.ShouldBe(RawMessageStatus.Error);
        Strings(partial, "notes").ShouldContain("Không nhận ra bản tin — kiểm giao thức / tốc độ baud của máy");
        partial.DecodeResult!.Value.GetProperty("error").GetString()!.ShouldContain("Không nhận ra bản tin");
        complete.DecodeStatus.ShouldBe(RawMessageStatus.NoResult);
    }

    // ---------------------------------------------------------------- I7

    [Fact]
    public async Task Giai_ma_lai_ra_it_dong_hon_them_canh_bao()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0031", "9.81", "132"), ("KHONGCO0032", "8.10", "120")));
        var first = await env.Processor.DecodeAsync(id, Auto, null, Ct);
        Prop(first, "unmatched").ShouldBe(4);
        Strings(first, "warnings").ShouldBeEmpty();

        // Giả lập cấu hình/bộ giải mã sai: bản tin giải ra ít dòng hơn.
        await ExecAsync(env, "UPDATE raw_message SET bytes = @bytes WHERE id = @id", new { id, bytes = RawEnv.Hl7(false, ("KHONGCO0031", "9.81", "132")) });

        var redo = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        Prop(redo, "supersededUnmatched").ShouldBe(4);
        Prop(redo, "unmatched").ShouldBe(2);
        Strings(redo, "warnings").ShouldContain("Giải mã lại ra ít dòng hơn lượt trước (4 → 2) — kiểm cấu hình máy");
    }

    [Fact]
    public async Task Giai_ma_lai_ra_du_dong_khong_co_canh_bao_it_dong()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0033", "9.81", "132")));
        await env.Processor.DecodeAsync(id, Auto, null, Ct);

        var redo = await env.Processor.DecodeAsync(id, Redo, env.Base.ActorId, Ct);

        Strings(redo, "warnings").ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- I9, M6

    private sealed class FixedLearning(params string[] notes) : IChannelMapLearning
    {
        public Task<IReadOnlyList<ChannelMapHint>> SuggestAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<ChannelMapHint>>([]);

        public Task<int> ConfirmAsync(Guid instrumentId, string channelCode, Guid serviceId, Guid? actorId) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> AutoLearnAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<string>>(notes);
    }

    [Fact]
    public async Task Tu_map_kenh_ghi_vet_vao_notes_cua_ban_tin_va_log_va_bo_NUL()
    {
        var learning = new FixedLearning("Tự map kênh 718-7 → HGB Huyết sắc tố (khớp ở 3 mẫu), đưa 2 kết quả vào phiếu", "Tự map kênh X\0 → Y");
        var env = await RawEnv.CreateAsync(fixture, learning: learning);
        var logs = new List<string>();
        var processor = new RawMessageProcessor(env.Db, env.Ingest, learning, env.Clock, logs.Add);
        var (_, barcode, _) = await env.NewSampleAsync();
        await ExecAsync(env, "DELETE FROM instrument_channel WHERE channel_code = '718-7'");
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        var row = await processor.DecodeAsync(id, Auto, null, Ct);

        var notes = Strings(row, "notes");
        notes.ShouldContain(n => n.StartsWith("Tự map kênh 718-7 → HGB"));
        notes.ShouldContain("Tự map kênh X → Y", "NUL bị lọc");
        logs.ShouldContain(l => l.Contains("Tự map kênh 718-7"));
        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
    }

    // ---------------------------------------------------------------- M5

    [Fact]
    public async Task Hang_rao_luot_chay_so_ca_so_lan_claim()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0041", "9.81", "132")));
        var claim = await env.Processor.ClaimAsync(id, Auto, Ct);
        claim.Attempts.ShouldBe(1);
        // Lượt khác đã claim lại cùng dấu thời gian (giả lập): số lần claim đã khác.
        await ExecAsync(env, "UPDATE raw_message SET decode_attempts = 2 WHERE id = @id", new { id });

        await env.Processor.RecordErrorAsync(claim.Row!, claim.ClaimedAt, Auto, null, null, new InvalidOperationException("Lỗi lượt cũ"), claim.Attempts);

        var after = await env.Store.FindAsync(id);
        after!.DecodeStatus.ShouldBe(RawMessageStatus.Decoding);
    }
}
