using Dapper;
using Lis.Data.RawMessages;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.RawMessages;

/// <summary>Kho bản tin thô: chèn chống trùng theo id agent, liệt kê, đánh dấu bị gián đoạn, xoá sau 90 ngày.</summary>
[Collection(PostgresCollection.Name)]
public class RawMessageStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Insert_trung_id_tra_null_khong_doi_dong_cu()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var first = env.Insert([1, 2, 3]);

        var stored = await env.Store.InsertIfAbsentAsync(first, TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(first.Id);
        stored.Bytes.ShouldBe([1, 2, 3]);
        stored.DecodeStatus.ShouldBe(RawMessageStatus.Waiting);
        stored.Seq.ShouldBe(1);

        var again = await env.Store.InsertIfAbsentAsync(first with { Bytes = [9, 9], Seq = 7 }, TestContext.Current.CancellationToken);
        again.ShouldBeNull();

        var kept = await env.Store.FindAsync(first.Id);
        kept.ShouldNotBeNull();
        kept.Bytes.ShouldBe([1, 2, 3]);
        kept.Seq.ShouldBe(1);
    }

    [Fact]
    public async Task List_loc_theo_trang_thai_va_phan_trang_theo_con_tro()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var insert = env.Insert([(byte)i]) with { CompletedAt = RawEnv.CompletedAt.AddMinutes(i) };
            await env.Store.InsertIfAbsentAsync(insert, TestContext.Current.CancellationToken);
            ids.Add(insert.Id);
        }

        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE raw_message SET decode_status = 'chi-luu' WHERE id = @id", new { id = ids[0] });
        }

        var page1 = await env.Store.ListAsync(new RawMessageFilter(env.InstrumentId, null, null, null, null, null, 2));
        page1.Select(r => r.Id).ShouldBe([ids[4], ids[3]]);

        var last = page1[^1];
        var page2 = await env.Store.ListAsync(new RawMessageFilter(env.InstrumentId, null, null, null, last.CompletedAt, last.Id, 10));
        page2.Select(r => r.Id).ShouldBe([ids[2], ids[1], ids[0]]);

        var filtered = await env.Store.ListAsync(new RawMessageFilter(null, [RawMessageStatus.StoredOnly], null, null, null, null, 500));
        filtered.Select(r => r.Id).ShouldBe([ids[0]]);
    }

    [Fact]
    public async Task Xoa_90_ngay_khong_xoa_ban_tin_chua_xong()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var byStatus = new Dictionary<string, Guid>();
        foreach (var status in new[] { "cho-giai-ma", "dang-giai-ma", "can-xem-lai", "da-giai-ma", "khong-co-ket-qua", "loi", "chi-luu" })
        {
            byStatus[status] = await env.StoreAsync([1]);
            await using var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = @status, stored_at = now() - interval '100 days' WHERE id = @id",
                new { status, id = byStatus[status] });
        }

        var recent = await env.StoreAsync([2]);
        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE raw_message SET decode_status = 'da-giai-ma' WHERE id = @id", new { id = recent });
        }

        var deleted = await env.Store.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-90));

        deleted.ShouldBe(4);
        foreach (var status in new[] { "cho-giai-ma", "dang-giai-ma", "can-xem-lai" })
        {
            (await env.Store.FindAsync(byStatus[status])).ShouldNotBeNull($"{status} phải được giữ");
        }

        foreach (var status in new[] { "da-giai-ma", "khong-co-ket-qua", "loi", "chi-luu" })
        {
            (await env.Store.FindAsync(byStatus[status])).ShouldBeNull($"{status} cũ phải bị xoá");
        }

        (await env.Store.FindAsync(recent)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Pending_cu_nhat_truoc_va_MarkInterrupted_chi_dang_giai_ma_qua_han()
    {
        var env = await RawEnv.CreateAsync(fixture);
        var newer = await env.StoreAsync([1]);
        var older = await env.StoreAsync([2]);
        var stuckOld = await env.StoreAsync([3]);
        var stuckNew = await env.StoreAsync([4]);
        await using (var connection = await env.Db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE raw_message SET stored_at = now() - interval '1 hour' WHERE id = @older", new { older });
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = now() - interval '10 minutes' WHERE id = @stuckOld",
                new { stuckOld });
            await connection.ExecuteAsync(
                "UPDATE raw_message SET decode_status = 'dang-giai-ma', decode_started_at = now() WHERE id = @stuckNew", new { stuckNew });
        }

        (await env.Store.PendingAsync(10)).ShouldBe([older, newer]);
        (await env.Store.PendingAsync(1)).ShouldBe([older]);

        (await env.Store.MarkInterruptedAsync(DateTimeOffset.UtcNow.AddMinutes(-5))).ShouldBe(1);
        (await env.Store.FindAsync(stuckOld))!.DecodeStatus.ShouldBe(RawMessageStatus.NeedsReview);
        (await env.Store.FindAsync(stuckNew))!.DecodeStatus.ShouldBe(RawMessageStatus.Decoding);
    }
}
