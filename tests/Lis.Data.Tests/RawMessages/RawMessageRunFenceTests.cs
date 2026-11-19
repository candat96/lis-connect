using System.Text.Json;
using Dapper;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.RawMessages;
using Npgsql;
using Shouldly;
using Xunit;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.RawMessages;

/// <summary>
/// Hàng rào lượt chạy, phục hồi không đoán theo thời gian (vòng sửa 1, mục C2 / I1), khoá mẫu theo thứ tự và thử lại lỗi
/// tạm thời (I2), xoá máy không cuốn theo bản tin thô chưa xong (I4), rollback không che lỗi gốc (I5).
/// </summary>
[Collection(PostgresCollection.Name)]
public class RawMessageRunFenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<RawMessageRow> StatusAsync(RawEnv env, Guid id) => (await env.Store.FindAsync(id))!;

    [Fact]
    public async Task Luot_chet_sau_claim_khoi_dong_lai_ngay_van_duoc_chay_lai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));

        // Tiến trình chết ngay sau khi claim (dang-giai-ma đã commit, giao dịch chính chưa kịp mở).
        var claim = await env.Processor.ClaimAsync(id, "tu-dong", Ct);
        claim.Skipped.ShouldBeNull();
        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Decoding);

        var restarted = new RawMessageProcessor(env.Db, env.Ingest, null, env.Clock);
        await restarted.RecoverAsync(Ct);
        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Decoding, "chưa qua thời gian ân hạn thì để nguyên");

        // Không chờ 5 phút: qua ân hạn (1 phút) là chạy lại.
        env.Clock.Advance(RawMessageProcessor.RecoverGrace + TimeSpan.FromSeconds(1));
        await restarted.RecoverAsync(Ct);

        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        (await HistoryAsync(env.Db, items[env.WbcId])).Count(h => h.Action == "nhan").ShouldBe(1);
    }

    [Fact]
    public async Task Luot_dang_giu_khoa_khong_bi_phuc_hoi_cuop()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0020", "9.81", "132")));
        await env.Processor.ClaimAsync(id, "tu-dong", Ct);
        env.Clock.Advance(TimeSpan.FromMinutes(10));

        // Lượt "đang sống": một giao dịch giữ FOR UPDATE trên dòng, dù đã quá hạn từ lâu.
        await using var holder = await env.Db.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await holder.ExecuteAsync("SELECT id FROM raw_message WHERE id = @id FOR UPDATE", new { id }, transaction);

        var skipped = await env.Store.RecoverAsync(RawMessageStore.DefaultRecoverGrace, Ct);

        skipped.Total.ShouldBe(0);
        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Decoding);

        await transaction.CommitAsync(Ct);
        var recovered = await env.Store.RecoverAsync(RawMessageStore.DefaultRecoverGrace, Ct);
        recovered.Requeued.ShouldBe(1);
        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
    }

    [Fact]
    public async Task Loi_sau_commit_khong_ghi_de_trang_thai_cuoi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var logs = new List<string>();
        var processor = new RawMessageProcessor(env.Db, env.Ingest, null, env.Clock, logs.Add);
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        var done = await processor.DecodeAsync(id, "tu-dong", null, Ct);
        done.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        var traces = await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id });

        // Commit mập mờ: lượt đã xong nhưng bên gọi tưởng lỗi và gọi ghi lỗi với claim cũ.
        await processor.RecordErrorAsync(done, done.DecodeStartedAt!.Value.UtcDateTime, "tu-dong", null, null, new InvalidOperationException("Lỗi sau commit giả"));

        var after = await StatusAsync(env, id);
        after.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        after.DecodeResult!.Value.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id })).ShouldBe(traces);
        logs.ShouldContain(l => l.Contains("mất quyền chạy"));
    }

    [Fact]
    public async Task Luot_cu_khong_ghi_de_luot_moi_sau_khi_bi_phuc_hoi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0021", "9.81", "132")));
        var oldClaim = await env.Processor.ClaimAsync(id, "tu-dong", Ct);
        env.Clock.Advance(TimeSpan.FromMinutes(5));
        await env.Store.RecoverAsync(RawMessageStore.DefaultRecoverGrace, Ct);
        var newClaim = await env.Processor.ClaimAsync(id, "tu-dong", Ct);
        newClaim.ClaimedAt.ShouldBeGreaterThan(oldClaim.ClaimedAt);

        // Lượt cũ (zombie) báo lỗi muộn: không đụng được lượt mới đang chạy.
        await env.Processor.RecordErrorAsync(oldClaim.Row!, oldClaim.ClaimedAt, "tu-dong", null, null, new InvalidOperationException("Lượt cũ"));

        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Decoding);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id })).ShouldBe(0);
    }

    [Fact]
    public async Task Ba_lan_gian_doan_thanh_can_xem_lai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var id = await env.StoreAsync(RawEnv.Hl7(false, ("KHONGCO0022", "9.81", "132")));
        var grace = RawMessageStore.DefaultRecoverGrace;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            (await env.Processor.ClaimAsync(id, "tu-dong", Ct)).Skipped.ShouldBeNull();
            env.Clock.Advance(grace + TimeSpan.FromSeconds(1));
            (await env.Store.RecoverAsync(grace, Ct)).ShouldBe(new RawMessageRecovery(1, 0));
            (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Waiting, $"lần gián đoạn thứ {attempt} còn tự chạy lại");
        }

        (await env.Processor.ClaimAsync(id, "tu-dong", Ct)).Skipped.ShouldBeNull();
        env.Clock.Advance(grace + TimeSpan.FromSeconds(1));
        (await env.Store.RecoverAsync(grace, Ct)).ShouldBe(new RawMessageRecovery(0, 1));

        var row = await StatusAsync(env, id);
        row.DecodeStatus.ShouldBe(RawMessageStatus.NeedsReview);
        row.DecodeResult!.Value.GetProperty("note").GetString().ShouldBe("Giải mã bị gián đoạn 3 lần");
        // can-xem-lai không bị lượt tự động nào đụng tới nữa.
        (await env.Processor.DecodeAsync(id, "tu-dong", null, Ct)).DecodeStatus.ShouldBe(RawMessageStatus.NeedsReview);
    }

    [Fact]
    public async Task Giai_ma_lai_bi_gian_doan_thanh_can_xem_lai_khong_tu_chay_lai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);
        await env.Processor.ClaimAsync(id, "giai-ma-lai", Ct);
        env.Clock.Advance(RawMessageStore.DefaultRecoverGrace + TimeSpan.FromSeconds(1));

        (await env.Store.RecoverAsync(RawMessageStore.DefaultRecoverGrace, Ct)).ShouldBe(new RawMessageRecovery(0, 1));

        var row = await StatusAsync(env, id);
        row.DecodeStatus.ShouldBe(RawMessageStatus.NeedsReview);
        row.DecodeResult!.Value.GetProperty("note").GetString().ShouldBe("Giải mã bị gián đoạn 2 lần");
    }

    [Fact]
    public async Task Hai_ban_tin_khoa_mau_nguoc_thu_tu_khong_deadlock()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var a = await env.NewSampleAsync("Mẫu A");
        var b = await env.NewSampleAsync("Mẫu B");
        var m1 = await env.StoreAsync(RawEnv.Hl7(false, (a.Barcode, "5.00", "110"), (b.Barcode, "6.00", "120")));
        var m2 = await env.StoreAsync(RawEnv.Hl7(false, (b.Barcode, "7.00", "130"), (a.Barcode, "8.00", "140")));

        // Đồng hồ thật: lần thử lại (nếu có) nghỉ thật. Hai bản tin đứng chờ nhau sau reading đầu để ép xen kẽ khoá.
        var system = TimeProvider.System;
        var logs = new List<string>();
        RawMessageProcessor Create() => new(env.Db, new IngestService(env.Db, system), null, system, logs.Add);
        var p1 = Create();
        var p2 = Create();
        var arrived = 0;
        var both = new TaskCompletionSource();
        Func<int, Task> barrier = async n =>
        {
            if (n != 1)
            {
                return;
            }

            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.TrySetResult();
            }

            // Có khoá thứ tự thì bản tin thứ hai bị chặn ở khoá mẫu nên không bao giờ tới đây: chờ có hạn rồi đi tiếp.
            await Task.WhenAny(both.Task, Task.Delay(TimeSpan.FromSeconds(3), Ct));
        };
        p1.AfterReadingIngested = barrier;
        p2.AfterReadingIngested = barrier;

        var rows = await Task.WhenAll(p1.DecodeAsync(m1, "tu-dong", null, Ct), p2.DecodeAsync(m2, "tu-dong", null, Ct));

        rows.Select(r => r.DecodeStatus).ShouldBe([RawMessageStatus.Decoded, RawMessageStatus.Decoded]);
        // Khoá thứ tự ngăn deadlock từ gốc: không được phải nhờ tới thử lại.
        logs.ShouldBeEmpty();
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE status = 'loi'")).ShouldBe(0);
        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(4);
    }

    [Fact]
    public async Task Loi_tam_thoi_thu_lai_giai_ma_lai_tu_byte_va_khong_ghi_trung()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, items) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        var calls = 0;
        env.Processor.AfterReadingIngested = n =>
        {
            calls++;
            return calls == 1 ? throw new NpgsqlException("Mất kết nối thử", new IOException("thử")) : Task.CompletedTask;
        };

        var decode = env.Processor.DecodeAsync(id, "tu-dong", null, Ct);
        await AdvanceUntilDoneAsync(env, decode);
        var row = await decode;

        row.DecodeStatus.ShouldBe(RawMessageStatus.Decoded);
        calls.ShouldBe(2);
        (await HistoryAsync(env.Db, items[env.WbcId])).Count(h => h.Action == "nhan").ShouldBe(1);
        (await env.CountAsync("SELECT count(*) FROM raw_message_decode WHERE raw_message_id = @id", new { id })).ShouldBe(1);
        (await env.CountAsync("SELECT decode_attempts FROM raw_message WHERE id = @id", new { id })).ShouldBe(1);
    }

    [Fact]
    public async Task Loi_tam_thoi_qua_3_lan_thu_lai_thi_ghi_loi()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        var calls = 0;
        env.Processor.AfterReadingIngested = _ =>
        {
            calls++;
            throw new NpgsqlException("Mất kết nối thử", new IOException("thử"));
        };

        var decode = env.Processor.DecodeAsync(id, "tu-dong", null, Ct);
        await AdvanceUntilDoneAsync(env, decode);
        var row = await decode;

        row.DecodeStatus.ShouldBe(RawMessageStatus.Error);
        calls.ShouldBe(4);
        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(0);
    }

    [Fact]
    public async Task Huy_khong_do_tat_may_chu_la_loi_khong_phai_thanh_cong()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        // Token của bên gọi KHÔNG bị huỷ: OperationCanceledException nội bộ (hết giờ...) xếp vào nhánh lỗi.
        env.Processor.AfterReadingIngested = _ => throw new OperationCanceledException("Hết giờ nội bộ");

        var row = await env.Processor.DecodeAsync(id, "tu-dong", null, Ct);

        row.DecodeStatus.ShouldBe(RawMessageStatus.Error);
        (await env.CountAsync("SELECT count(*) FROM result")).ShouldBe(0);
    }

    [Fact]
    public async Task Gian_doan_do_tat_may_chu_co_bao_khong_tinh_la_mot_lan_that_bai()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var (_, barcode, _) = await env.NewSampleAsync();
        var id = await env.StoreAsync(RawEnv.Hl7(false, (barcode, "9.81", "132")));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        env.Processor.AfterReadingIngested = _ =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => env.Processor.DecodeAsync(id, "tu-dong", null, shutdown.Token));

        (await StatusAsync(env, id)).DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
        (await env.CountAsync("SELECT decode_attempts FROM raw_message WHERE id = @id", new { id })).ShouldBe(0);
    }

    [Fact]
    public async Task Xoa_may_con_ban_tin_chua_xong_bi_chan()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var finished = await env.StoreAsync([1]);
        var waiting = await env.StoreAsync([2]);
        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE raw_message SET decode_status = 'da-giai-ma' WHERE id = @finished", new { finished });
        }

        var result = await new InstrumentStore(env.Db).DeleteAsync(env.InstrumentId);

        result.ShouldBe(InstrumentDeleteResult.InUseRawMessages);
        (await env.Store.FindAsync(waiting)).ShouldNotBeNull();
        (await env.Store.FindAsync(finished)).ShouldNotBeNull("bị chặn thì không xoá gì, kể cả bản tin đã xong");
        (await env.CountAsync("SELECT count(*) FROM instrument WHERE id = @id", new { id = env.InstrumentId })).ShouldBe(1);
    }

    [Fact]
    public async Task Xoa_may_chi_con_ban_tin_da_xong_thi_xoa_ca_ban_tin()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var decoded = await env.StoreAsync([1]);
        var noResult = await env.StoreAsync([2]);
        await using (var connection = await env.Db.OpenAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE raw_message SET decode_status = 'da-giai-ma' WHERE id = @decoded", new { decoded });
            await connection.ExecuteAsync("UPDATE raw_message SET decode_status = 'khong-co-ket-qua' WHERE id = @noResult", new { noResult });
        }

        var result = await new InstrumentStore(env.Db).DeleteAsync(env.InstrumentId);

        result.ShouldBe(InstrumentDeleteResult.Deleted);
        (await env.CountAsync("SELECT count(*) FROM raw_message")).ShouldBe(0);
        (await env.CountAsync("SELECT count(*) FROM instrument WHERE id = @id", new { id = env.InstrumentId })).ShouldBe(0);
    }

    [Fact]
    public async Task Khoa_ngoai_bo_cascade_xoa_thang_may_khong_cuon_theo_ban_tin_tho()
    {
        var env = await RawEnv.CreateAsync(fixture);
        await env.StoreAsync([1]);
        await using var connection = await env.Db.OpenAsync(Ct);

        var ex = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM instrument WHERE id = @id", new { id = env.InstrumentId }));

        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task InTransaction_loi_goc_khong_bi_che_khi_rollback_hong()
    {
        var db = Db.Create(await fixture.NewDatabaseAsync());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => db.InTransactionAsync<int>(async (connection, _) =>
        {
            // Đóng kết nối giữa giao dịch: rollback sau đó sẽ hỏng.
            await connection.CloseAsync();
            throw new InvalidOperationException("Lỗi gốc thử");
        }));

        ex.Message.ShouldBe("Lỗi gốc thử");
    }

    /// <summary>Đồng hồ giả không tự chạy: đẩy tới khi lượt giải mã (đang nghỉ giữa các lần thử lại) xong.</summary>
    private static async Task AdvanceUntilDoneAsync(RawEnv env, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "lượt giải mã không kết thúc");
            env.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(20, Ct);
        }
    }
}
