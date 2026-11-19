using System.Net;
using Lis.Connections.RawMessages;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>
/// Gửi bản tin thô từ hàng chờ trên đĩa lên server, lần lượt từng máy, theo <c>Seq</c>, mỗi lô tối đa <see cref="BatchMessages"/> bản tin;
/// lô sau chỉ gửi khi lô trước đã xong. Tệp chỉ bị xoá khi server xác nhận (<c>da-luu</c> / <c>trung</c>).
/// </summary>
/// <remarks>
/// <c>khong-hop-le</c> → đổi tệp thành <c>.loi</c>. <c>khong-thuoc-agent</c> → giữ tệp, máy đó tạm bỏ qua <see cref="NotOwnedPause"/>.
/// Lỗi mạng / 5xx → lùi nhịp 1, 2, 4 … 60 giây (±20 %). 401 → dừng hẳn, không xoá gì.
/// Khoá agent chỉ nằm ở tiêu đề của <see cref="ServerClient"/>, không bao giờ vào log hay nội dung bản tin.
/// </remarks>
public sealed class RawMessageUploader(RawMessageQueue queue, ServerClient client, TimeProvider clock, ILogger logger)
{
    public const int BatchMessages = 50;

    /// <summary>Tổng byte (đã giải base64) mỗi lô; server giới hạn 16 MB. Luôn gửi ít nhất một bản tin.</summary>
    public const long BatchBytes = 12 * 1024 * 1024;

    public static readonly TimeSpan NotOwnedPause = TimeSpan.FromMinutes(5);

    /// <summary>Nhịp dò hàng chờ khi rảnh.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private enum Outcome
    {
        Done,
        Failed,
        Revoked,
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, DateTimeOffset> _skipUntil = [];
    private DateTimeOffset _retryAt;
    private int _failures;
    private volatile bool _revoked;

    /// <summary>Vòng gửi nền: dò hàng chờ mỗi <see cref="PollInterval"/>, tôn trọng lùi nhịp và tạm bỏ qua.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_revoked)
        {
            try
            {
                await TickAsync(ct);
                await Task.Delay(PollInterval, clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Vòng gửi bản tin thô gặp lỗi bất ngờ: {Message}", ex.Message);
                try
                {
                    await Task.Delay(PollInterval, clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Gửi ngay theo yêu cầu (lệnh <c>flush-pending</c>): bỏ qua lùi nhịp và tạm bỏ qua. Trả số bản tin còn trong hàng.</summary>
    public Task<int> FlushNowAsync(CancellationToken ct) => PassAsync(force: true, ct);

    /// <summary>Một lượt gửi theo lịch (lùi nhịp, tạm bỏ qua có hiệu lực). Trả số bản tin còn trong hàng.</summary>
    public Task<int> TickAsync(CancellationToken ct) => PassAsync(force: false, ct);

    private async Task<int> PassAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_revoked && (force || clock.GetUtcNow() >= _retryAt))
            {
                foreach (var instrumentId in queue.Instruments())
                {
                    if (!force && _skipUntil.TryGetValue(instrumentId, out var until) && clock.GetUtcNow() < until)
                    {
                        continue;
                    }

                    if (await SendInstrumentAsync(instrumentId, ct) != Outcome.Done)
                    {
                        break; // mạng / server lỗi hoặc bị thu hồi: các máy khác cũng sẽ vướng như vậy
                    }
                }
            }

            return queue.Count();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Outcome> SendInstrumentAsync(Guid instrumentId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = TakeBatch(queue.Peek(instrumentId, BatchMessages));
            if (batch.Count == 0)
            {
                _skipUntil.Remove(instrumentId);
                return Outcome.Done;
            }

            RawMessagesResponse response;
            try
            {
                response = await client.SendRawMessagesAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _revoked = true;
                logger.LogError("Server từ chối khoá agent (401) — dừng gửi bản tin thô, giữ nguyên hàng chờ trên đĩa");
                return Outcome.Revoked;
            }
            catch (Exception ex)
            {
                Fail($"Chưa gửi được {batch.Count} bản tin của máy {instrumentId:N}: {ex.Message}");
                return Outcome.Failed;
            }

            var results = new Dictionary<Guid, RawMessageResult>();
            foreach (var result in response.Results ?? [])
            {
                if (result.Id is { } id)
                {
                    results.TryAdd(id, result);
                }
            }

            var progressed = false;
            var notOwned = false;
            foreach (var message in batch)
            {
                if (!results.TryGetValue(message.Id, out var result))
                {
                    continue; // server không nhắc tới: coi như chưa xác nhận, giữ tệp
                }

                switch (result.Status)
                {
                    case "da-luu":
                    case "trung":
                        progressed |= TryDelete(message);
                        break;
                    case "khong-hop-le":
                        progressed |= TryMarkInvalid(message);
                        break;
                    case "khong-thuoc-agent":
                        notOwned = true;
                        break;
                    default:
                        logger.LogWarning("Server trả trạng thái lạ \"{Status}\" cho bản tin {Id}; giữ tệp", result.Status, message.Id);
                        break;
                }
            }

            if (progressed)
            {
                _failures = 0;
                _retryAt = default;
            }

            if (notOwned)
            {
                _skipUntil[instrumentId] = clock.GetUtcNow() + NotOwnedPause;
                logger.LogWarning(
                    "Server báo máy {Instrument} không thuộc agent này — giữ bản tin, thử lại sau {Minutes} phút", instrumentId.ToString("N"), NotOwnedPause.TotalMinutes);
                return Outcome.Done;
            }

            if (!progressed)
            {
                Fail($"Gửi {batch.Count} bản tin của máy {instrumentId:N} nhưng không xử lý được bản tin nào");
                return Outcome.Failed;
            }
        }
    }

    private static List<RawMessage> TakeBatch(IReadOnlyList<RawMessage> candidates)
    {
        var batch = new List<RawMessage>();
        long bytes = 0;
        foreach (var message in candidates)
        {
            if (batch.Count > 0 && (batch.Count >= BatchMessages || bytes + message.Bytes.Length > BatchBytes))
            {
                break;
            }

            batch.Add(message);
            bytes += message.Bytes.Length;
        }

        return batch;
    }

    /// <summary>Xoá tệp đã được server xác nhận. Antivirus có thể đang giữ tệp: lỗi này để nhịp sau (server sẽ trả <c>trung</c>).</summary>
    private bool TryDelete(RawMessage message)
    {
        try
        {
            queue.Delete(message);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Chưa xoá được tệp bản tin {Id} đã gửi xong ({Message}) — thử lại nhịp sau", message.Id, ex.Message);
            return false;
        }
    }

    private bool TryMarkInvalid(RawMessage message)
    {
        try
        {
            queue.MarkInvalid(message);
            logger.LogError(
                "Server từ chối bản tin {Id} (máy {Instrument}, Seq {Seq}) vì không hợp lệ — đã đổi tên tệp thành .loi để xem tay",
                message.Id, message.InstrumentId.ToString("N"), message.Seq);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Chưa đổi được tên tệp bản tin {Id} thành .loi ({Message}) — thử lại nhịp sau", message.Id, ex.Message);
            return false;
        }
    }

    private void Fail(string reason)
    {
        _failures++;
        var delay = BackoffFor(_failures, Random.Shared.NextDouble());
        _retryAt = clock.GetUtcNow() + delay;
        logger.LogWarning("{Reason} — thử lại sau {Seconds:0.#} giây", reason, delay.TotalSeconds);
    }

    /// <summary>1, 2, 4 … tối đa 60 giây, nhân hệ số ngẫu nhiên trong khoảng ±20 % (<paramref name="random01"/> ∈ [0, 1]).</summary>
    public static TimeSpan BackoffFor(int failures, double random01)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Max(0, failures - 1)));
        return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * random01));
    }
}
