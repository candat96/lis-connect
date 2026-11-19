using System.Text;
using Dapper;
using Lis.Data.Ingest;
using Lis.Data.RawMessages;
using Lis.Data.Tests.Results;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.RawMessages;

/// <summary>
/// Giải mã bản tin thô ở server: mọi reading đi qua IngestService trong MỘT giao dịch cùng dòng vết;
/// lỗi giữa chừng rollback hết; giải mã lại không chèn trùng; mẫu đã duyệt chỉ nhận "chờ thay".
/// </summary>
[Collection(PostgresCollection.Name)]
public class RawMessageProcessorTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> RowsOfMessageAsync(RawEnv env, Guid id)
    {
        var rawRef = $"ban-tin:{id}";
        return await env.CountAsync(
            """
            SELECT (SELECT count(*) FROM result_history h JOIN result r ON r.id = h.result_id WHERE r.raw_ref = @rawRef OR r.pending_raw_ref = @rawRef)
                 + (SELECT count(*) FROM qc_result WHERE raw_ref = @rawRef)
                 + (SELECT count(*) FROM unmatched_result WHERE raw_ref = @rawRef)
            """,
            new { rawRef });
    }

    [Fact]
    public async Task Decode_hl7_ghi_ket_qua_qua_ingest_va_raw_ref_ban_tin()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        row.DecoderVersion.ShouldNotBeNullOrEmpty();
        row.DecodedAt.ShouldNotBeNull();
        row.ProtocolFamily.ShouldBe("hl7-mllp");
        row.Model.ShouldBe("BC-5380");
        row.DecodeResult.ShouldNotBeNull();
        row.DecodeResult.Value.GetProperty("readings").GetInt32().ShouldBe(1);
        row.DecodeResult.Value.GetProperty("stored").GetInt32().ShouldBe(2);
        row.DecodeResult.Value.GetProperty("error").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("9.81");
        wbc.RawRef.ShouldBe($"ban-tin:{id}");
        wbc.InstrumentId.ShouldBe(env.InstrumentId);
        (await StoredAsync(env.Db, items[env.HgbId])).Value.ShouldBe("132");

        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'tu-dong' AND status = 'da-giai-ma'", new { id }))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Decode_mau_da_duyet_chi_cho_thay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (sampleId, barcode, items) = await env.NewSampleAsync();
        await env.Base.Results.SetManualAsync(items[env.WbcId], "5.00", env.Base.ActorId, null);
        await env.Base.Results.SetManualAsync(items[env.HgbId], "120", env.Base.ActorId, null);
        await env.Base.Results.ApproveAsync(sampleId, env.Base.ActorId);
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        var row = await env.Processor.DecodeAsync(id, "giai-ma-lai", env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        row.DecodeResult!.Value.GetProperty("pendingReplacement").GetInt32().ShouldBe(2);
        row.DecodeResult.Value.GetProperty("stored").GetInt32().ShouldBe(0);
        var wbc = await StoredAsync(env.Db, items[env.WbcId]);
        wbc.Value.ShouldBe("5.00");
        wbc.Status.ShouldBe("da-duyet");
        wbc.PendingValue.ShouldBe("9.81");
        wbc.PendingRawRef.ShouldBe($"ban-tin:{id}");
    }

    [Fact]
    public async Task Giai_ma_lai_qc_va_chua_ghep_khong_chen_trung()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var qcId = await env.StoreAsync(RawEnv.Hl7(true, ("QC-THU-01", "6.10", "140")));
        // Barcode không có mẫu -> chưa ghép (khong-co-barcode).
        var unmatchedId = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0001", "7.00", "100")));
        var mixedId = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        await env.Processor.DecodeAsync(qcId, "tu-dong", null, Ct);
        await env.Processor.DecodeAsync(unmatchedId, "tu-dong", null, Ct);
        await env.Processor.DecodeAsync(mixedId, "tu-dong", null, Ct);

        (await env.CountAsync("SELECT count(*) FROM qc_result")).ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result")).ShouldBe(2);
        var historyBefore = (await HistoryAsync(env.Db, items[env.WbcId])).Count;

        // Giải mã lại 2 lần nữa: số dòng QC/chưa ghép không đổi và Outcome không đếm dòng bị bỏ qua.
        for (var i = 0; i < 2; i++)
        {
            var qcRow = await env.Processor.DecodeAsync(qcId, "giai-ma-lai", env.Base.ActorId, Ct);
            qcRow.DecodeResult!.Value.GetProperty("qc").GetInt32().ShouldBe(0);
            var unmatchedRow = await env.Processor.DecodeAsync(unmatchedId, "giai-ma-lai", env.Base.ActorId, Ct);
            unmatchedRow.DecodeResult!.Value.GetProperty("unmatched").GetInt32().ShouldBe(0);
            await env.Processor.DecodeAsync(mixedId, "giai-ma-lai", env.Base.ActorId, Ct);
        }

        (await env.CountAsync("SELECT count(*) FROM qc_result")).ShouldBe(2);
        (await env.CountAsync("SELECT count(*) FROM unmatched_result")).ShouldBe(2);
        (await StoredAsync(env.Db, items[env.WbcId])).Value.ShouldBe("9.81");
        (await HistoryAsync(env.Db, items[env.WbcId])).Count.ShouldBeGreaterThanOrEqualTo(historyBefore);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @qcId", new { qcId })).ShouldBe(3);
    }

    [Fact]
    public async Task Ingest_nem_loi_o_reading_thu_hai_rollback_het_trang_thai_loi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var s1 = await env.NewSampleAsync("Mẫu Một");
        var s2 = await env.NewSampleAsync("Mẫu Hai");
        var s3 = await env.NewSampleAsync("Mẫu Ba");
        var id = await env.StoreAsync(RawEnv.Hl7(false, (s1.Barcode, "5.1", "110"), (s2.Barcode, "6.2", "120"), (s3.Barcode, "7.3", "130")));
        env.Processor.AfterReadingIngested = n => n == 2 ? throw new InvalidOperationException("Hỏng thử ở reading thứ 2") : Task.CompletedTask;

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Error);
        row.DecodeResult!.Value.GetProperty("error").GetString()!.ShouldContain("Hỏng thử ở reading thứ 2");
        row.DecodeResult.Value.GetProperty("stored").GetInt32().ShouldBe(0);
        (await RowsOfMessageAsync(env, id)).ShouldBe(0);
        (await StoredOrNullAsync(env.Db, s1.Items[env.WbcId])).ShouldBeNull();
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND status = 'loi'", new { id })).ShouldBe(1);
    }

    [Fact]
    public async Task Server_chet_giua_chung_cho_giai_ma_duoc_giai_ma_dung_mot_lan()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var s1 = await env.NewSampleAsync("Mẫu Một");
        var s2 = await env.NewSampleAsync("Mẫu Hai");
        var s3 = await env.NewSampleAsync("Mẫu Ba");
        var id = await env.StoreAsync(RawEnv.Hl7(false, (s1.Barcode, "5.1", "110"), (s2.Barcode, "6.2", "120"), (s3.Barcode, "7.3", "130")));

        // "Server chết": tiến trình bị huỷ sau reading thứ 2 trong 3 (tắt máy chủ giữa chừng).
        env.Processor.AfterReadingIngested = n => n == 2 ? throw new OperationCanceledException("Tắt máy chủ") : Task.CompletedTask;
        var interrupted = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        interrupted.DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
        (await RowsOfMessageAsync(env, id)).ShouldBe(0);
        (await StoredOrNullAsync(env.Db, s1.Items[env.WbcId])).ShouldBeNull();
        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(0);

        // "Khởi động lại": bộ xử lý mới, không còn hook; RecoverAsync giải mã đúng một lần.
        var restarted = new RawMessageProcessor(env.Db, env.Ingest, null, env.Clock);
        await restarted.RecoverAsync(Ct);
        await restarted.RecoverAsync(Ct);

        (await env.Store.FindAsync(id))!.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        foreach (var s in new[] { s1, s2, s3 })
        {
            foreach (var serviceId in new[] { env.WbcId, env.HgbId })
            {
                var history = await HistoryAsync(env.Db, s.Items[serviceId]);
                history.Count(h => h.Action == "nhan").ShouldBe(1);
            }
        }

        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(6);
        (await env.CountAsync("SELECT count(*) FROM result_history WHERE action = 'nhan'")).ShouldBe(6);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND status = 'da-giai-ma'", new { id })).ShouldBe(1);
    }

    [Fact]
    public async Task Server_chet_giua_chung_qc_ghi_dung_mot_lan()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(true, ("QC-A", "5.1", "110"), ("QC-B", "6.2", "120"), ("QC-C", "7.3", "130")));
        env.Processor.AfterReadingIngested = n => n == 2 ? throw new OperationCanceledException("Tắt máy chủ") : Task.CompletedTask;

        await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);
        (await env.CountAsync("SELECT count(*) FROM qc_result")).ShouldBe(0);

        await new RawMessageProcessor(env.Db, env.Ingest, null, env.Clock).RecoverAsync(Ct);

        (await env.CountAsync("SELECT count(*) FROM qc_result")).ShouldBe(6);
        (await env.CountAsync("SELECT count(*) FROM qc_result WHERE barcode = 'QC-A'")).ShouldBe(2);
    }

    [Fact]
    public async Task Dang_giai_ma_qua_5_phut_thanh_can_xem_lai_khong_tu_chay()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        // Giả lập tiến trình chết sau khi đã commit "dang-giai-ma": bắt đầu từ 10 phút trước.
        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = @at WHERE id = @id",
                new { id, at = env.Clock.GetUtcNow().AddMinutes(-10).ToUniversalTime() });
        }

        await env.Processor.RecoverAsync(Ct);

        (await env.Store.FindAsync(id))!.DecodeStatus.ShouldBe(RawMessageStatus.NeedsReview);
        (await StoredOrNullAsync(env.Db, items[env.WbcId])).ShouldBeNull();

        // Chưa quá 5 phút thì để nguyên (có thể lượt khác còn đang chạy).
        var fresh = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = @at WHERE id = @id",
                new { id = fresh, at = env.Clock.GetUtcNow().AddMinutes(-2).ToUniversalTime() });
        }

        await env.Processor.RecoverAsync(Ct);
        (await env.Store.FindAsync(fresh))!.DecodeStatus.ShouldBe(RawMessageStatus.Decoding);
    }

    [Fact]
    public async Task Dang_giai_ma_do_luot_khac_thi_bo_qua_va_tra_nguyen_dong()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = @at WHERE id = @id",
                new { id, at = env.Clock.GetUtcNow().ToUniversalTime() });
        }

        var row = await env.Processor.DecodeAsync(id, "giai-ma-lai", env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoding);
        (await StoredOrNullAsync(env.Db, items[env.WbcId])).ShouldBeNull();
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode")).ShouldBe(0);
    }

    [Fact]
    public async Task Chi_nghe_chi_luu()
    {
        var env = await RawEnv.CreateAsync(fixture, family: "chi-nghe");
        var id = await env.StoreAsync([0x01, 0x02, 0x03]);

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.StoredOnly);
        row.DecodeResult!.Value.GetProperty("readings").GetInt32().ShouldBe(0);
        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(0);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND status = 'chi-luu'", new { id })).ShouldBe(1);

        // Máy chưa chọn giao thức cũng chỉ lưu.
        var blank = await RawEnv.CreateAsync(fixture, family: null);
        var blankId = await blank.StoreAsync([0x01]);
        (await blank.Processor.DecodeAsync(blankId, "tu-dong", null, Ct)).DecodeStatus.ShouldBe(RawMessageStatus.StoredOnly);
    }

    [Fact]
    public async Task Ban_tin_hong_checksum_khong_sinh_ket_qua_trang_thai_khong_co_ket_qua_hoac_loi()
    {
        var env = await RawEnv.CreateAsync(fixture, family: "astm-e1394");
        var (_, _, items) = await env.NewSampleAsync();
        // ENQ, khung STX 1 ... ETX với checksum sai "00", rồi EOT.
        var body = Encoding.ASCII.GetBytes("1H|\\^&|||Máy Thử\rP|1\rR|1|^^^WBC|9.81|G/L\rL|1|N\r");
        byte[] bytes = [0x05, 0x02, .. body, 0x03, (byte)'0', (byte)'0', 0x0D, 0x0A, 0x04];
        var id = await env.StoreAsync(bytes);

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBeOneOf(RawMessageStatus.NoResult, RawMessageStatus.Error);
        (await RowsOfMessageAsync(env, id)).ShouldBe(0);
        (await StoredOrNullAsync(env.Db, items[env.WbcId])).ShouldBeNull();
    }

    [Fact]
    public async Task Ban_tin_khong_co_ket_qua_la_khong_co_ket_qua_khong_phai_loi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync([0x0B, .. Encoding.UTF8.GetBytes("MSH|^~\\&|A|B|||20261119||ACK^R01|1|P|2.3.1\r"), 0x1C, 0x0D]);

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.NoResult);
    }

    [Fact]
    public async Task Xem_thu_khong_ghi_gi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        var preview = await env.Processor.PreviewAsync(id);

        preview.Readings.ShouldHaveSingleItem().Items.Count.ShouldBe(2);
        (await StoredOrNullAsync(env.Db, items[env.WbcId])).ShouldBeNull();
        (await env.Store.FindAsync(id))!.DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode")).ShouldBe(0);
    }

    [Fact]
    public async Task Tu_hoc_map_kenh_chay_sau_commit_khi_co_chua_ghep_va_loi_chi_ghi_log()
    {
        var learning = new CountingLearning();
        var env = await RawEnv.CreateAsync(fixture, learning: learning);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0002", "7.00", "100")));

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        learning.Calls.ShouldBe([env.InstrumentId]);

        var failing = await RawEnv.CreateAsync(fixture, learning: new CountingLearning { Throw = true });
        var failId = await failing.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0003", "7.00", "100")));
        (await failing.Processor.DecodeAsync(failId, "tu-dong", null, Ct)).DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
    }

    [Fact]
    public async Task Giai_ma_lai_dung_cau_hinh_hien_tai_va_vet_ghi_ai_lam()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE instrument SET protocol_family = 'chi-nghe' WHERE id = @id", new { id = env.InstrumentId });
        }

        var row = await env.Processor.DecodeAsync(id, "giai-ma-lai", env.Base.ActorId, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.StoredOnly);
        var traces = (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id }));
        traces.ShouldBe(2);
        (await env.CountAsync(
            "SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id AND kind = 'giai-ma-lai' AND actor_id = @actor AND decoder_version <> ''",
            new { id, actor = env.Base.ActorId })).ShouldBe(1);
    }

    private sealed class CountingLearning : IChannelMapLearning
    {
        public List<Guid> Calls { get; } = [];

        public bool Throw { get; init; }

        public Task<IReadOnlyList<ChannelMapHint>> SuggestAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<ChannelMapHint>>([]);

        public Task<int> ConfirmAsync(Guid instrumentId, string channelCode, Guid serviceId, Guid? actorId) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> AutoLearnAsync(Guid instrumentId)
        {
            Calls.Add(instrumentId);
            return Throw ? throw new InvalidOperationException("Hỏng thử") : Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
