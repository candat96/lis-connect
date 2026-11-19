using System.Diagnostics;
using Lis.Connections.RawMessages;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

public class RawMessageQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lis-rawq-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Inst = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(7));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static RawMessage Msg(long seq, byte[]? bytes = null, Guid? inst = null) =>
        new(Guid.CreateVersion7(), inst ?? Inst, seq, "phien", T0, T0.AddSeconds(1), bytes ?? [0x0B, 0x41, 0x1C, 0x0D], false);

    [Fact]
    public async Task Queue_ghi_nguyen_tu_va_doc_theo_seq()
    {
        var q = new RawMessageQueue(_root);
        var m3 = Msg(30);
        var m1 = Msg(10, [1, 2, 3, 255]);
        var m2 = Msg(20);
        foreach (var m in new[] { m3, m1, m2 })
        {
            await q.SaveAsync(m, TestContext.Current.CancellationToken);
        }

        var dir = Path.Combine(_root, Inst.ToString("N"));
        Directory.GetFiles(dir, "*.tmp").ShouldBeEmpty();
        Directory.GetFiles(dir, "*.json").Length.ShouldBe(3);
        Path.GetFileName(Directory.GetFiles(dir, "*.json").Order().First()).ShouldBe($"{10:D20}-{m1.Id:N}.json");
        File.ReadAllText(Directory.GetFiles(dir, "*.json").First(f => f.Contains(m1.Id.ToString("N")))).ShouldContain("\"bytes\":\"AQID/w==\"");

        var peek = q.Peek(Inst, 10);
        peek.Select(m => m.Seq).ShouldBe([10, 20, 30]);
        peek[0].Bytes.ShouldBe(new byte[] { 1, 2, 3, 255 });
        peek[0].Id.ShouldBe(m1.Id);
        peek[0].StartedAt.ShouldBe(T0);
        q.Peek(Inst, 2).Count.ShouldBe(2);
        q.Count().ShouldBe(3);
        q.Instruments().ShouldBe([Inst]);

        q.Delete(m1);
        q.Peek(Inst, 10).Select(m => m.Seq).ShouldBe([20, 30]);
        q.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Queue_tep_hong_doi_ten_loi()
    {
        var q = new RawMessageQueue(_root);
        await q.SaveAsync(Msg(10), TestContext.Current.CancellationToken);
        var dir = Path.Combine(_root, Inst.ToString("N"));
        var bad = Path.Combine(dir, $"{15:D20}-{Guid.NewGuid():N}.json");
        File.WriteAllText(bad, "{ hỏng");
        await q.SaveAsync(Msg(20), TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(dir, "x.json.tmp"), "dở");

        q.Peek(Inst, 10).Select(m => m.Seq).ShouldBe([10, 20]);
        File.Exists(bad).ShouldBeFalse();
        File.Exists(bad + ".loi").ShouldBeTrue();
        q.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Queue_nhieu_may_tach_thu_muc()
    {
        var q = new RawMessageQueue(_root);
        var other = Guid.NewGuid();
        await q.SaveAsync(Msg(1), TestContext.Current.CancellationToken);
        await q.SaveAsync(Msg(1, inst: other), TestContext.Current.CancellationToken);
        q.Instruments().Order().ShouldBe(new[] { Inst, other }.Order());
        q.Peek(other, 10).Count.ShouldBe(1);
        new RawMessageQueue(Path.Combine(_root, "khong-co")).Peek(Inst, 5).ShouldBeEmpty();
    }

    [Fact]
    public async Task Queue_SaveAsync_do_thoi_gian_ghi_dia()
    {
        var q = new RawMessageQueue(_root);
        var payload = new byte[2048];
        await q.SaveAsync(Msg(0, payload), TestContext.Current.CancellationToken); // khởi động
        var sw = new Stopwatch();
        var times = new List<double>();
        for (var i = 1; i <= 20; i++)
        {
            sw.Restart();
            await q.SaveAsync(Msg(i, payload), TestContext.Current.CancellationToken);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"SAVEASYNC_MS trungbinh={times.Average():F1} max={times.Max():F1} min={times.Min():F1}");
        times.Max().ShouldBeLessThan(2000);
    }
}
