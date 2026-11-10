using System.Collections.Concurrent;
using Lis.Core.Instruments;
using Lis.Data.Instruments;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>
/// Hàng chờ log đẩy lên server. Agent ghi mọi byte đi/về với máy và mọi sự kiện vào đây; một vòng
/// nền gửi theo lô. Mất mạng thì giữ lại trong bộ nhớ (tối đa <see cref="Capacity"/> dòng, đầy thì
/// bỏ dòng cũ nhất) — bản đầy đủ luôn có trên đĩa agent (thư mục rawlog).
/// </summary>
public sealed class LogUploader(ServerClient server, ILogger<LogUploader> logger)
{
    public const int Capacity = 20_000;
    private const int BatchSize = 300;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<AgentLogLine> _queue = new();
    private int _dropped;

    public void Enqueue(AgentLogLine line)
    {
        _queue.Enqueue(line);
        while (_queue.Count > Capacity && _queue.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Cảnh báo / thông tin của chính agent (không gắn với máy nào).</summary>
    public void AgentNote(string note) =>
        Enqueue(new AgentLogLine(null, DateTimeOffset.Now, "agent", null, null, note));

    /// <summary>Gửi theo lô cho tới khi bị huỷ. Lỗi gửi: giữ nguyên hàng chờ, thử lại lượt sau.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<AgentLogLine>(BatchSize);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
                while (!ct.IsCancellationRequested)
                {
                    batch.Clear();
                    foreach (var line in _queue)
                    {
                        batch.Add(line);
                        if (batch.Count == BatchSize)
                        {
                            break;
                        }
                    }

                    if (batch.Count == 0)
                    {
                        break;
                    }

                    await server.SendLogsAsync(batch, ct);
                    // Gửi xong mới lấy khỏi hàng chờ — lỗi giữa chừng không mất dòng nào.
                    for (var i = 0; i < batch.Count; i++)
                    {
                        _queue.TryDequeue(out _);
                    }

                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                    {
                        AgentNote($"Mất mạng lâu: đã bỏ {dropped} dòng log cũ khỏi hàng chờ gửi (vẫn còn trên đĩa agent)");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Chưa gửi được log lên server: {Message}", ex.Message);
            }
        }
    }
}

/// <summary>Log thô của agent: ghi xuống đĩa như cũ (<see cref="RawLogStore"/>) và đưa vào hàng chờ đẩy lên server.</summary>
public sealed class UploadingRawLogStore(RawLogStore local, LogUploader uploader) : IRawLogStore
{
    public string RootDirectory => local.RootDirectory;

    public void Append(Guid instrumentId, RawLogEntry entry)
    {
        local.Append(instrumentId, entry);
        uploader.Enqueue(new AgentLogLine(
            instrumentId,
            entry.Timestamp,
            entry.Direction switch { RawLogDirection.In => "in", RawLogDirection.Out => "out", _ => "event" },
            entry.Session,
            entry.Bytes.Length == 0 ? null : Convert.ToHexString(entry.Bytes),
            entry.Note));
    }

    public IReadOnlyList<DateOnly> ListDates(Guid instrumentId) => local.ListDates(instrumentId);

    public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date) => local.Read(instrumentId, date);

    public string FilePath(Guid instrumentId, DateOnly date) => local.FilePath(instrumentId, date);
}
