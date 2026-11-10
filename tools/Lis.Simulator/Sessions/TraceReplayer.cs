using System.Diagnostics;
using Lis.Core.Instruments;
using Lis.Simulator.Links;

namespace Lis.Simulator.Sessions;

/// <summary>Thống kê một lần phát lại.</summary>
public sealed record ReplayStats(string Session, int BlocksSent, int BytesSent, int BytesReceived);

/// <summary>
/// Phát lại tệp <c>.listrace</c>: gửi các bản ghi chiều "in" (máy → LIS) theo thứ tự, giữ khoảng
/// thời gian gốc (chia cho <c>speed</c>; speed 0 = không chờ), sau mỗi khối đọc và in byte LIS trả về.
/// Bản ghi chiều "out" không gửi; chỉ để biết LIS đã trả gì hồi đó (in kèm để so) và để biết có nên
/// chờ trả lời (khối kế tiếp là "out" → chờ tối đa <see cref="ReplyWait"/>).
/// </summary>
public sealed class TraceReplayer(IMachineLink link, Action<string> log)
{
    /// <summary>Chờ trả lời tối đa khi log gốc cho thấy LIS có trả lời ngay sau khối này.</summary>
    public TimeSpan ReplyWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Chờ ngắn khi log gốc không có trả lời (vẫn đọc để in nếu LIS có gửi).</summary>
    public TimeSpan QuietWait { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Đọc tệp .listrace; dòng hỏng bị bỏ qua.</summary>
    public static IReadOnlyList<RawLogEntry> Load(string path) =>
        [.. File.ReadLines(path).Select(RawLogFormat.TryParse).OfType<RawLogEntry>()];

    public async Task<ReplayStats> RunAsync(IReadOnlyList<RawLogEntry> entries, string? session, double speed, CancellationToken ct)
    {
        session ??= entries.FirstOrDefault(e => e.Direction == RawLogDirection.In)?.Session;
        if (session is null)
        {
            log("Tệp không có bản ghi chiều máy → LIS (in) nào để phát.");
            return new ReplayStats("", 0, 0, 0);
        }

        var list = entries.Where(e => e.Session == session && e.Direction != RawLogDirection.Event && e.Bytes.Length > 0).ToList();
        log($"Phát lại phiên '{session}': {list.Count(e => e.Direction == RawLogDirection.In)} khối máy → LIS, tốc độ {(speed > 0 ? $"x{speed:0.##}" : "tối đa (không chờ)")}.");

        var clock = Stopwatch.StartNew();
        var start = list.Count > 0 ? list[0].Timestamp : DateTimeOffset.Now;
        int blocks = 0, sent = 0, received = 0;

        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry.Direction != RawLogDirection.In)
            {
                continue;
            }

            if (speed > 0)
            {
                var due = TimeSpan.FromTicks((long)((entry.Timestamp - start).Ticks / speed));
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
            }

            blocks++;
            sent += entry.Bytes.Length;
            log($"→ LIS  [{blocks}] {RawBytes.ToReadable(entry.Bytes).TrimEnd()}");
            await link.WriteAsync(entry.Bytes, ct);

            // Các bản ghi "out" liền sau: LIS gốc đã trả gì.
            var expected = new List<RawLogEntry>();
            for (var j = i + 1; j < list.Count && list[j].Direction == RawLogDirection.Out; j++)
            {
                expected.Add(list[j]);
            }

            var data = await link.ReadAsync(expected.Count > 0 ? ReplyWait : QuietWait, ct);
            if (data.Length > 0)
            {
                received += data.Length;
                log($"← LIS  {RawBytes.ToReadable(data).TrimEnd()}");
            }
            else if (expected.Count > 0)
            {
                log("← LIS  (không trả lời trong thời hạn)");
            }

            if (expected.Count > 0)
            {
                log($"       (log gốc LIS đã trả: {string.Join(" ", expected.Select(e => RawBytes.ToReadable(e.Bytes).TrimEnd()))})");
            }
        }

        var tail = await link.ReadAsync(QuietWait * 5, ct);
        if (tail.Length > 0)
        {
            received += tail.Length;
            log($"← LIS  {RawBytes.ToReadable(tail).TrimEnd()}");
        }

        return new ReplayStats(session, blocks, sent, received);
    }
}
