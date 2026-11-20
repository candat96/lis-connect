using System.Net;
using Lis.Connections.RawMessages;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>Kết quả lệnh gửi ngay: số bản tin còn trong hàng; <see cref="Busy"/> = vòng nền đang gửi, không chờ được cổng.</summary>
public readonly record struct FlushResult(int Remaining, bool Busy);

/// <summary>
/// Gửi bản tin thô từ hàng chờ trên đĩa lên server, lần lượt từng máy, theo <c>Seq</c>, mỗi lô tối đa <see cref="BatchMessages"/> bản tin;
/// lô sau chỉ gửi khi lô trước đã xong. Tệp chỉ bị xoá khi server xác nhận (<c>da-luu</c> / <c>trung</c>).
/// </summary>
/// <remarks>
/// <para>
/// Thứ tự gửi được giữ chặt: server ingest đường <c>Live</c> theo thứ tự đến, bản tin cũ đến sau bản tin mới sẽ ghi giá trị cũ đè lên
/// kết quả chạy lại. Vì vậy trong một máy, bản tin nào chưa được xác nhận hoặc chưa xoá được (tệp bị khoá, <c>Delete</c> lỗi IO,
/// server bỏ sót Id) thì các bản sau nó không được xoá/gửi tiếp trong lượt đó.
/// </para>
/// <para>
/// <c>khong-hop-le</c> → đổi tệp thành <c>.loi</c>. Bản tin lớn hơn <see cref="MaxMessageBytes"/> → không gửi, đổi <c>.loi</c> luôn.
/// <c>khong-thuoc-agent</c> → giữ tệp, máy đó tạm bỏ qua <see cref="NotOwnedPause"/>.
/// Lỗi 4xx (400, 404, 413 …) và "không xử lý được bản tin nào" → lùi nhịp 1, 2, 4 … 60 giây (±20 %) <b>riêng máy đó</b>, máy khác vẫn gửi.
/// Mất kết nối / 5xx → lùi nhịp cả lượt (server hỏng thì máy nào cũng hỏng). 401 → dừng hẳn, không xoá gì.
/// Khoá agent chỉ nằm ở tiêu đề của <see cref="ServerClient"/>, không bao giờ vào log hay nội dung bản tin.
/// </para>
/// </remarks>
public sealed class RawMessageUploader(RawMessageQueue queue, ServerClient client, TimeProvider clock, ILogger logger)
{
    public const int BatchMessages = 50;

    /// <summary>Tổng byte (đã giải base64) mỗi lô; server giới hạn 16 MB. Luôn gửi ít nhất một bản tin.</summary>
    public const long BatchBytes = 12 * 1024 * 1024;

    /// <summary>Bản tin lớn hơn mức này server từ chối (<c>khong-hop-le</c>): không gửi. Cùng hằng số với bộ gom.</summary>
    public const int MaxMessageBytes = RawMessageCollector.MaxMessageBytes;

    public static readonly TimeSpan NotOwnedPause = TimeSpan.FromMinutes(5);

    /// <summary>Nhịp dò hàng chờ khi rảnh.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Lệnh gửi ngay chờ vòng nền nhả cổng tối đa chừng này, quá thì trả <see cref="FlushResult.Busy"/> (không giữ kênh lệnh lâu).</summary>
    public static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);

    private enum Outcome
    {
        /// <summary>Hết bản tin để gửi cho máy này (hoặc máy tạm bỏ qua).</summary>
        Done,

        /// <summary>Máy này vướng (4xx, server không xác nhận, xoá tệp lỗi): đã lùi nhịp riêng, máy khác vẫn gửi.</summary>
        MachineFailed,

        /// <summary>Mạng / 5xx: đã lùi nhịp cả lượt.</summary>
        Failed,
        Revoked,
    }

    private sealed class MachineState
    {
        public int Failures;
        public DateTimeOffset RetryAt;
    }

    /// <summary>Cỡ lô hiện tại của một máy (đường truyền chậm thì nhỏ lại) và số lô liên tiếp đã xong.</summary>
    private sealed class BatchSizer
    {
        public int Limit = BatchMessages;
        public int Streak;
    }

    /// <summary>Số lô thành công liên tiếp trước khi nới cỡ lô gấp đôi.</summary>
    private const int GrowAfter = 3;

    private static readonly TimeSpan BlockedWarnEvery = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, BatchSizer> _sizers = [];
    private readonly Dictionary<Guid, DateTimeOffset> _blockedWarned = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, MachineState> _machines = [];
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

    /// <summary>
    /// Gửi ngay theo yêu cầu (lệnh <c>flush-pending</c>): bỏ qua lùi nhịp và tạm bỏ qua. Cổng đang bận (vòng nền đang gửi) thì chờ tối đa
    /// <see cref="FlushWait"/>, quá thì trả ngay số bản tin còn lại kèm <see cref="FlushResult.Busy"/>.
    /// </summary>
    public async Task<FlushResult> FlushNowAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(FlushWait, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FlushResult(queue.Count(), true);
        }

        try
        {
            return new FlushResult(await PassLockedAsync(force: true, ct), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Một lượt gửi theo lịch (lùi nhịp, tạm bỏ qua có hiệu lực). Trả số bản tin còn trong hàng.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await PassLockedAsync(force: false, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> PassLockedAsync(bool force, CancellationToken ct)
    {
        if (!_revoked && (force || clock.GetUtcNow() >= _retryAt))
        {
            foreach (var instrumentId in queue.Instruments())
            {
                if (!force && _machines.TryGetValue(instrumentId, out var state) && clock.GetUtcNow() < state.RetryAt)
                {
                    continue;
                }

                if (await SendInstrumentAsync(instrumentId, ct) is Outcome.Failed or Outcome.Revoked)
                {
                    break; // mạng / server lỗi hoặc bị thu hồi: các máy khác cũng sẽ vướng như vậy
                }
            }
        }

        return queue.Count();
    }

    private async Task<Outcome> SendInstrumentAsync(Guid instrumentId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var limit = _sizers.TryGetValue(instrumentId, out var sizer) ? sizer.Limit : BatchMessages;
            var (batch, blocked, dropped) = TakeBatch(instrumentId, queue.Peek(instrumentId, limit), limit);
            if (batch.Count == 0)
            {
                if (blocked)
                {
                    MachineFail(instrumentId, $"Chưa đổi được tên tệp bản tin quá lớn của máy {instrumentId:N}");
                    return Outcome.MachineFailed;
                }

                if (dropped)
                {
                    continue; // vừa loại bản tin quá lớn: đọc lại hàng, có thể còn bản tin gửi được phía sau
                }

                WarnIfHeadLocked(instrumentId);
                _machines.Remove(instrumentId);
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Hết giờ HTTP: lô quá nặng cho đường truyền (hoặc server giải mã lâu) — gửi lại đúng lô đó mãi thì không bao giờ xong.
                var smaller = Shrink(instrumentId);
                FailAll($"Hết giờ khi gửi {batch.Count} bản tin của máy {instrumentId:N} (lô sau tối đa {smaller} bản tin)");
                return Outcome.Failed;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            {
                // 408 / 429: server (hoặc proxy) quá tải — lùi cả lượt như lỗi mạng, không phải lỗi riêng của máy này.
                FailAll($"Server báo quá tải ({(int)ex.StatusCode!.Value}) khi gửi {batch.Count} bản tin của máy {instrumentId:N}");
                return Outcome.Failed;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && (int)code is >= 400 and < 500)
            {
                // 400, 404, 413 … : lỗi do lô của máy này, không phải server hỏng — chỉ lùi nhịp máy này.
                MachineFail(instrumentId, $"Server từ chối lô {batch.Count} bản tin của máy {instrumentId:N}: {ex.Message}");
                return Outcome.MachineFailed;
            }
            catch (Exception ex)
            {
                FailAll($"Chưa gửi được {batch.Count} bản tin của máy {instrumentId:N}: {ex.Message}");
                return Outcome.Failed;
            }

            // Server trả lời được: hết lùi nhịp cả lượt.
            _failures = 0;
            _retryAt = default;

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
            var ioStuck = false;
            foreach (var message in batch)
            {
                // Giữ thứ tự: gặp bản tin chưa xác nhận / chưa xoá được thì dừng, các bản sau nó ở lại (lần sau gửi lại, server báo trung).
                if (!results.TryGetValue(message.Id, out var result))
                {
                    break; // server không nhắc tới (kể cả Id lạ trong kết quả bị bỏ qua)
                }

                if (result.Status is "da-luu" or "trung")
                {
                    if (!TryDelete(message))
                    {
                        ioStuck = true;
                        break;
                    }
                }
                else if (result.Status == "khong-hop-le")
                {
                    if (!TryMarkInvalid(message))
                    {
                        ioStuck = true;
                        break;
                    }
                }
                else if (result.Status == "khong-thuoc-agent")
                {
                    notOwned = true;
                    break;
                }
                else
                {
                    logger.LogWarning("Server trả trạng thái lạ \"{Status}\" cho bản tin {Id}; giữ tệp", result.Status, message.Id);
                    break;
                }

                progressed = true;
            }

            if (progressed)
            {
                _machines.Remove(instrumentId);
                Grow(instrumentId);
            }

            if (notOwned)
            {
                _machines[instrumentId] = new MachineState { RetryAt = clock.GetUtcNow() + NotOwnedPause };
                logger.LogWarning(
                    "Server báo máy {Instrument} không thuộc agent này — giữ bản tin, thử lại sau {Minutes} phút (còn {Count} bản tin; chuyển máy về agent này hoặc liên hệ hỗ trợ)",
                    instrumentId.ToString("N"), NotOwnedPause.TotalMinutes, queue.Count(instrumentId));
                return Outcome.Done;
            }

            if (ioStuck)
            {
                MachineFail(instrumentId, $"Không xoá/đổi tên được tệp bản tin của máy {instrumentId:N}, dừng gửi tiếp các bản sau để giữ thứ tự");
                return Outcome.MachineFailed;
            }

            if (!progressed)
            {
                MachineFail(instrumentId, $"Gửi {batch.Count} bản tin của máy {instrumentId:N} nhưng không xử lý được bản tin nào");
                return Outcome.MachineFailed;
            }
        }
    }

    /// <summary>
    /// Chọn các bản tin gửi được theo thứ tự. Bản tin quá lớn không bao giờ gửi được: đổi <c>.loi</c> ngay (<c>dropped</c>); đổi tên lỗi IO thì
    /// dừng ở đó (<c>blocked</c>) để các bản sau không đi trước nó.
    /// </summary>
    private (List<RawMessage> Batch, bool Blocked, bool Dropped) TakeBatch(Guid instrumentId, IReadOnlyList<RawMessage> candidates, int limit)
    {
        var byteLimit = BatchBytes * limit / BatchMessages;
        var batch = new List<RawMessage>();
        var blocked = false;
        var dropped = false;
        long bytes = 0;
        foreach (var message in candidates)
        {
            if (message.Bytes.Length > MaxMessageBytes)
            {
                try
                {
                    queue.MarkInvalid(message);
                    dropped = true;
                    logger.LogError(
                        "Bản tin quá lớn ({Bytes} byte) — không gửi được, đã chuyển .loi (máy {Instrument}, Seq {Seq})",
                        message.Bytes.Length, instrumentId.ToString("N"), message.Seq);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Chưa đổi được tên tệp bản tin quá lớn {Id} thành .loi ({Message}) — thử lại nhịp sau", message.Id, ex.Message);
                    blocked = true;
                    break;
                }
            }

            if (batch.Count > 0 && (batch.Count >= limit || bytes + message.Bytes.Length > byteLimit))
            {
                break;
            }

            batch.Add(message);
            bytes += message.Bytes.Length;
        }

        return (batch, blocked, dropped);
    }

    /// <summary>Hết giờ: nửa cỡ lô của máy (tối thiểu 1 bản tin). Trả cỡ mới.</summary>
    private int Shrink(Guid instrumentId)
    {
        if (!_sizers.TryGetValue(instrumentId, out var sizer))
        {
            _sizers[instrumentId] = sizer = new BatchSizer();
        }

        sizer.Limit = Math.Max(1, sizer.Limit / 2);
        sizer.Streak = 0;
        return sizer.Limit;
    }

    /// <summary>Một lô xong: đủ <see cref="GrowAfter"/> lô liên tiếp thì nới gấp đôi (tối đa 50 bản tin / 12 MB).</summary>
    private void Grow(Guid instrumentId)
    {
        if (!_sizers.TryGetValue(instrumentId, out var sizer))
        {
            return;
        }

        if (++sizer.Streak < GrowAfter)
        {
            return;
        }

        sizer.Streak = 0;
        sizer.Limit = Math.Min(BatchMessages, sizer.Limit * 2);
        if (sizer.Limit >= BatchMessages)
        {
            _sizers.Remove(instrumentId);
        }
    }

    /// <summary>Peek trả rỗng mà hàng còn bản tin = tệp đầu hàng đang bị khoá: báo (tối đa 1 lần/phút/máy) để biết vì sao hàng đứng yên.</summary>
    private void WarnIfHeadLocked(Guid instrumentId)
    {
        var count = queue.Count(instrumentId);
        if (count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        if (_blockedWarned.TryGetValue(instrumentId, out var last) && now - last < BlockedWarnEvery)
        {
            return;
        }

        _blockedWarned[instrumentId] = now;
        logger.LogWarning(
            "Hàng chờ máy {Instrument} còn {Count} bản tin nhưng chưa đọc được tệp đầu hàng ({File}) — tệp có thể đang bị khoá (diệt vi-rút / sao lưu)",
            instrumentId.ToString("N"), count, queue.FirstFileName(instrumentId) ?? "?");
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

    /// <summary>Lùi nhịp cả lượt (mất kết nối, 5xx).</summary>
    private void FailAll(string reason)
    {
        _failures++;
        var delay = BackoffFor(_failures, Random.Shared.NextDouble());
        _retryAt = clock.GetUtcNow() + delay;
        logger.LogWarning("{Reason} — thử lại sau {Seconds:0.#} giây", reason, delay.TotalSeconds);
    }

    /// <summary>Lùi nhịp riêng một máy (4xx, server không xác nhận, xoá tệp lỗi).</summary>
    private void MachineFail(Guid instrumentId, string reason)
    {
        if (!_machines.TryGetValue(instrumentId, out var state))
        {
            _machines[instrumentId] = state = new MachineState();
        }

        state.Failures++;
        var delay = BackoffFor(state.Failures, Random.Shared.NextDouble());
        state.RetryAt = clock.GetUtcNow() + delay;
        logger.LogWarning("{Reason} — máy này thử lại sau {Seconds:0.#} giây", reason, delay.TotalSeconds);
    }

    /// <summary>1, 2, 4 … tối đa 60 giây, nhân hệ số ngẫu nhiên trong khoảng ±20 % (<paramref name="random01"/> ∈ [0, 1]).</summary>
    public static TimeSpan BackoffFor(int failures, double random01)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Max(0, failures - 1)));
        return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * random01));
    }
}
