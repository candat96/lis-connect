using System.Text;
using System.Text.Json;
using Lis.Connections.RawMessages;
using Lis.Core.Drivers;
using Lis.Core.Ingest;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.Connections;

/// <summary>Trạng thái đường truyền của một máy.</summary>
public enum LinkState
{
    /// <summary>Không chạy trên máy trạm này (chưa cấu hình, máy trạm khác, chưa có driver…).</summary>
    NotRunning,

    /// <summary>Đang mở cổng / chờ máy nối vào.</summary>
    Connecting,

    Connected,

    /// <summary>Lỗi hoặc mất kết nối — đang chờ để thử lại.</summary>
    Error,
}

/// <summary>Trạng thái hiển thị của một máy ở màn Máy &amp; map.</summary>
public sealed record InstrumentConnectionStatus(
    Guid InstrumentId, LinkState State, string Text, DateTimeOffset? LastResultAt = null, int SamplesReceived = 0, int PendingToRetry = 0);

/// <summary>Nơi nhận bản tin kết quả đã tách (thật: <see cref="IngestService"/>). Ném ngoại lệ khi chưa ghi được (vd mất CSDL).</summary>
public interface IReadingSink
{
    Task<IngestOutcome> IngestAsync(InstrumentReading reading);

    /// <summary>Sau khi có kênh chưa map: thử tự tìm kênh ↔ dịch vụ theo barcode. Trả về các dòng ghi log.</summary>
    Task<IReadOnlyList<string>> LearnChannelsAsync(Guid instrumentId) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Cầu nối tới <see cref="IngestService"/>.</summary>
public sealed class IngestReadingSink(IngestService ingest, IChannelMapLearning? learning = null) : IReadingSink
{
    public Task<IngestOutcome> IngestAsync(InstrumentReading reading) => ingest.IngestAsync(reading);

    public Task<IReadOnlyList<string>> LearnChannelsAsync(Guid instrumentId) =>
        learning?.AutoLearnAsync(instrumentId) ?? Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Chạy kết nối của một máy: mở đường truyền (tự thử lại mỗi <see cref="RetryDelay"/> khi lỗi/đứt),
/// đưa byte vào driver, gửi phản hồi driver yêu cầu, ghi log thô hai chiều, chuyển bản tin kết quả
/// vào CSDL, trả lời máy hỏi chỉ định.
/// </summary>
/// <remarks>
/// Driver đã ACK thì máy coi như đã giao xong — nên kết quả không ghi được vào CSDL (mất mạng tới
/// CSDL…) được lưu xuống đĩa (<see cref="PendingDirectory"/>) và gửi lại định kỳ, kể cả sau khi
/// mở lại phần mềm. Mọi lời gọi driver đi qua một khoá (driver không an toàn đa luồng).
/// </remarks>
public sealed class InstrumentRunner : IAsyncDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly InstrumentInfo _instrument;
    private readonly ConnectionSettings _settings;
    private readonly IInstrumentDriver _driver;
    private readonly Func<ConnectionSettings, IByteLink> _linkFactory;
    private readonly IRawLogStore _rawLogs;
    private readonly IReadingSink _sink;
    private readonly IOrderQueryService _queries;
    private readonly TimeProvider _clock;
    private readonly Action<InstrumentConnectionStatus> _onStatus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _retryGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private IByteLink? _link;
    private string _session = "";
    private DateTimeOffset _nextPendingRetry;
    private InstrumentConnectionStatus _status;
    private readonly IRawMessageSink? _raw;
    private readonly RawMessageCollector? _collector;
    private readonly List<RawMessage> _unsaved = [];
    private long _unsavedBytes;
    private string? _rawFailureText;

    /// <summary>Trần cảnh báo của hàng giữ trong bộ nhớ khi đĩa lỗi (đang có bản tin chưa ghi thì runner đã ngắt máy, hàng không phình thêm).</summary>
    private const int MaxUnsavedMessages = 1000;

    private const long MaxUnsavedBytes = 50L * 1024 * 1024;
    private const int FinalFlushAttempts = 5;
    private static readonly TimeSpan FinalFlushGap = TimeSpan.FromMilliseconds(500);
    private const string RawStoreDownText = "Không ghi được đĩa — đã ngắt máy để máy tự giữ kết quả";

    /// <summary>Nội bộ: không ghi được bản tin thô xuống đĩa. Vòng ngoài coi như mất kết nối (máy không được ACK).</summary>
    private sealed class RawStoreUnavailableException(string message) : Exception(message);

    public InstrumentRunner(
        InstrumentInfo instrument,
        ConnectionSettings settings,
        IInstrumentDriver driver,
        Func<ConnectionSettings, IByteLink> linkFactory,
        IRawLogStore rawLogs,
        IReadingSink sink,
        IOrderQueryService queries,
        string pendingRoot,
        TimeProvider clock,
        Action<InstrumentConnectionStatus> onStatus,
        IRawMessageSink? rawSink = null)
    {
        _raw = rawSink;
        if (rawSink is not null)
        {
            // Chế độ bản tin thô: khoảng lặng cắt bản tin của "chi-nghe" là 2 giây, họ khác 60 giây.
            _collector = new RawMessageCollector(
                instrument.Id, clock, driver.Family == Lis.Core.Drivers.ListenOnly.ListenOnlyDriver.FamilyCode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(60),
                initialSeq: rawSink.MaxSeq(instrument.Id));
        }

        _instrument = instrument;
        _settings = settings;
        _driver = driver;
        _linkFactory = linkFactory;
        _rawLogs = rawLogs;
        _sink = sink;
        _queries = queries;
        _clock = clock;
        _onStatus = onStatus;
        PendingDirectory = Path.Combine(pendingRoot, instrument.Id.ToString("N"));
        _status = new InstrumentConnectionStatus(instrument.Id, LinkState.Connecting, "Đang khởi động…");
    }

    /// <summary>Thư mục kết quả chờ ghi lại vào CSDL của máy này.</summary>
    public string PendingDirectory { get; }

    public InstrumentConnectionStatus Status => _status;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await FinalRawFlushAsync();
        _stop.Dispose();
        _gate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Còn bản tin chưa xuống đĩa thì chưa nhận gì từ máy: lưu cho xong rồi mới mở kết nối.
            if (!await DrainUnsavedAsync(ct))
            {
                return;
            }

            _rawFailureText = null;
            _session = _clock.GetLocalNow().ToString("HHmmss");
            await using var link = _linkFactory(_settings);
            try
            {
                SetStatus(LinkState.Connecting, _settings.Kind == ConnectionKind.TcpServer ? $"Đang chờ máy nối vào · {link.Description}" : $"Đang mở · {link.Description}");
                await link.OpenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogEvent($"Không mở được kết nối: {ex.Message}");
                SetStatus(LinkState.Error, $"Không mở được: {ex.Message} — thử lại sau {RetryDelay.TotalSeconds:0} giây");
                await DelayAsync(ct);
                continue;
            }

            _link = link;
            await WithGateAsync(() => { _driver.Reset(); _collector?.NewSession(); return Task.CompletedTask; });
            LogEvent($"Đã kết nối · {link.Description}");
            SetStatus(LinkState.Connected, $"Đã kết nối · {link.Description}");

            using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var ticker = TickLoopAsync(sessionStop, ct);
            try
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var read = await link.ReadAsync(buffer, sessionStop.Token);
                    if (read == 0)
                    {
                        LogEvent("Máy đóng kết nối");
                        break;
                    }

                    var bytes = buffer.AsSpan(0, read).ToArray();
                    SafeLog(new RawLogEntry(_clock.GetLocalNow(), RawLogDirection.In, _session, bytes));
                    await WithGateAsync(() =>
                    {
                        var now = _clock.GetLocalNow();
                        // Byte vào bộ gom TRƯỚC Receive: Receive ném lỗi cũng không mất khúc byte.
                        _collector?.Append(bytes, now);
                        return HandleTopAsync(_driver.Receive(bytes, now), ct, bytes.Length);
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RawStoreUnavailableException ex)
            {
                _rawFailureText = ex.Message;
                LogEvent($"Mất kết nối: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogEvent(_rawFailureText is null ? $"Mất kết nối: {ex.Message}" : $"Mất kết nối: {_rawFailureText}");
            }
            finally
            {
                _link = null;
                await sessionStop.CancelAsync();
                try
                {
                    await ticker;
                }
                catch (Exception)
                {
                    // Vòng Tick tự bắt và log lỗi; dù sao cũng không được chặn việc xả bộ gom.
                }

                await FlushRawOnDisconnectAsync();
            }

            SetStatus(LinkState.Error, _rawFailureText is null
                ? $"Mất kết nối — thử lại sau {RetryDelay.TotalSeconds:0} giây"
                : $"{_rawFailureText} — thử lại sau {RetryDelay.TotalSeconds:0} giây");
            await DelayAsync(ct);
        }
    }

    /// <summary>
    /// Nhịp 1 giây: Tick driver, gửi bù hàng chờ. Lỗi bất kỳ được bắt và log (runner không chết, bộ gom không mất), trừ
    /// huỷ và "không ghi được đĩa": lỗi đó ngắt phiên (<paramref name="session"/>) để máy không được ACK khi chưa lưu.
    /// </summary>
    private async Task TickLoopAsync(CancellationTokenSource session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        var failing = false;
        while (await timer.WaitForNextTickAsync(session.Token))
        {
            try
            {
                await WithGateAsync(() => HandleTopAsync(_driver.Tick(_clock.GetLocalNow()), session.Token, null));
                if (_clock.GetUtcNow() >= _nextPendingRetry)
                {
                    _nextPendingRetry = _clock.GetUtcNow() + PendingRetryInterval;
                    await RetryPendingAsync();
                }

                failing = false;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested || ct.IsCancellationRequested)
            {
                return;
            }
            catch (RawStoreUnavailableException ex)
            {
                _rawFailureText = ex.Message;
                LogEvent($"Ngắt kết nối: {ex.Message}");
                await session.CancelAsync();
                return;
            }
            catch (Exception ex)
            {
                if (!failing)
                {
                    LogEvent($"Lỗi ở nhịp Tick (bỏ qua, vẫn chạy tiếp): {ex.Message}");
                }

                failing = true;
            }
        }
    }

    private async Task WithGateAsync(Func<Task> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Đầu ra của một lần gọi driver ở vòng chính (Receive hoặc Tick): ở chế độ bản tin thô, cắt và ghi đĩa bản tin
    /// TRƯỚC khi <see cref="HandleAsync"/> ghi bất kỳ byte nào ra máy. <paramref name="receivedLength"/> null = Tick.
    /// Còn bản tin chưa xuống đĩa thì KHÔNG ghi <c>Send</c> (máy không được ACK thứ chưa lưu): log ghi chú rồi ném
    /// <see cref="RawStoreUnavailableException"/> để vòng ngoài ngắt kết nối.
    /// </summary>
    private async Task HandleTopAsync(DriverOutput output, CancellationToken ct, int? receivedLength)
    {
        if (_collector is not null)
        {
            var now = _clock.GetLocalNow();
            var messages = receivedLength is { } length
                ? _collector.ApplyBoundaries(length, output.Boundaries, now, _driver.IsReceiving)
                : _collector.OnTick(output.Boundaries, now, _driver.IsReceiving);
            if (!await StoreRawAsync(messages, ct))
            {
                foreach (var note in output.Notes)
                {
                    LogEvent(note);
                }

                SetStatus(LinkState.Error, RawStoreDownText);
                throw new RawStoreUnavailableException(RawStoreDownText);
            }
        }

        await HandleAsync(output, ct);
    }

    /// <summary>
    /// Ghi các bản tin hoàn tất (và mọi bản tin còn tồn) theo thứ tự. Trả false nếu còn bản tin chưa xuống đĩa
    /// (ghi lỗi): bản tin giữ trong bộ nhớ, không ném. Chỉ ném khi bị huỷ.
    /// </summary>
    private async Task<bool> StoreRawAsync(IReadOnlyList<RawMessage> fresh, CancellationToken ct)
    {
        foreach (var message in fresh)
        {
            _unsaved.Add(message);
            _unsavedBytes += message.Bytes.Length;
        }

        while (_unsaved.Count > 0)
        {
            // Luôn thử ghi trước: kiểm trần trước khi ghi sẽ kẹt vĩnh viễn khi hàng giữ đã vượt trần mà đĩa đã phục hồi.
            // Còn bản tin chưa ghi là runner đã ngắt máy, không nhận thêm — trần chỉ còn là cảnh báo.
            try
            {
                await _raw!.SaveAsync(_unsaved[0], ct);
                _unsavedBytes -= _unsaved[0].Bytes.Length;
                _unsaved.RemoveAt(0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogEvent($"Chưa ghi được bản tin thô xuống đĩa ({ex.Message}) — giữ trong bộ nhớ ({_unsaved.Count} bản tin)");
                if (_unsaved.Count > MaxUnsavedMessages || _unsavedBytes > MaxUnsavedBytes)
                {
                    LogEvent($"Hàng bản tin chưa ghi đĩa vượt trần ({_unsaved.Count} bản tin, {_unsavedBytes} byte)");
                }

                return false;
            }
        }

        return true;
    }

    /// <summary>Trước khi mở kết nối: lưu cho hết hàng giữ, lỗi thì chờ <see cref="RetryDelay"/>. False = runner đang dừng.</summary>
    private async Task<bool> DrainUnsavedAsync(CancellationToken ct)
    {
        if (_raw is null)
        {
            return true;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var done = false;
                await WithGateAsync(async () => done = await StoreRawAsync([], ct), ct);
                if (done)
                {
                    return true;
                }

                SetStatus(LinkState.Error, $"{RawStoreDownText} — chưa nhận dữ liệu từ máy, thử lại sau {RetryDelay.TotalSeconds:0} giây");
                await DelayAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        return false;
    }

    private async Task FlushRawOnDisconnectAsync()
    {
        if (_collector is null)
        {
            return;
        }

        // Không ném: bản tin chưa ghi được ở lại _unsaved, vòng thử lại ở đầu RunAsync xử lý.
        await WithGateAsync(() => StoreRawAsync(_collector.OnDisconnect(_clock.GetLocalNow()), CancellationToken.None));
    }

    /// <summary>Dừng runner: xả hàng giữ tối đa 5 lần cách nhau 500 ms; vẫn lỗi thì log Critical số bản tin và số byte mất (không log nội dung).</summary>
    private async Task FinalRawFlushAsync()
    {
        if (_collector is null)
        {
            return;
        }

        for (var attempt = 1; attempt <= FinalFlushAttempts; attempt++)
        {
            bool done = false;
            await WithGateAsync(async () => done = await StoreRawAsync([], CancellationToken.None));
            if (done)
            {
                return;
            }

            if (attempt < FinalFlushAttempts)
            {
                await Task.Delay(FinalFlushGap, _clock);
            }
        }

        var lost = $"[Critical] Dừng runner khi đĩa vẫn lỗi: mất {_unsaved.Count} bản tin thô, tổng {_unsavedBytes} byte (chưa ghi xuống đĩa)";
        LogEvent(lost);
        // Log thô nằm trên chính cái đĩa đang lỗi — thêm kênh không phụ thuộc đĩa để còn dấu vết.
        System.Diagnostics.Trace.TraceError(lost);
        try { Console.Error.WriteLine(lost); } catch (IOException) { }
    }

    /// <summary>Xử lý đầu ra driver: ghi chú → log; khối gửi → máy; kết quả → CSDL; câu hỏi chỉ định → tra rồi trả lời.</summary>
    internal async Task HandleAsync(DriverOutput output, CancellationToken ct)
    {
        foreach (var note in output.Notes)
        {
            LogEvent(note);
        }

        foreach (var block in output.Send)
        {
            if (_link is null)
            {
                LogEvent("Bỏ khối gửi máy vì đã mất kết nối");
                break;
            }

            await _link.WriteAsync(block, ct);
            SafeLog(new RawLogEntry(_clock.GetLocalNow(), RawLogDirection.Out, _session, block));
        }

        // Chế độ bản tin thô: kết quả do server giải mã từ bản tin, không đi đường này.
        foreach (var reading in _raw is null ? output.Readings : [])
        {
            await SubmitAsync(new InstrumentReading(
                _instrument.Id, reading.Barcode, reading.IsQc, _clock.GetLocalNow(), Truncate(reading.RawText, 8000), reading.Items));
        }

        foreach (var query in output.Queries)
        {
            IReadOnlyList<string> channels;
            try
            {
                channels = await _queries.ChannelsToRunAsync(_instrument.Id, query.Barcode, ct);
            }
            catch (Exception ex)
            {
                // Không tra được CSDL: trả "không có chỉ định" để máy không treo chờ; ghi lại để biết.
                LogEvent($"Máy hỏi chỉ định mẫu {query.Barcode} nhưng không tra được CSDL: {ex.Message}");
                channels = [];
            }

            LogEvent(channels.Count == 0
                ? $"Máy hỏi chỉ định mẫu {query.Barcode}: không có chỉ định trên máy này"
                : $"Máy hỏi chỉ định mẫu {query.Barcode}: gửi {channels.Count} kênh ({string.Join(", ", channels)})");
            await HandleAsync(_driver.AnswerQuery(query, channels, _clock.GetLocalNow()), ct);
        }
    }

    private async Task SubmitAsync(InstrumentReading reading)
    {
        try
        {
            var outcome = await _sink.IngestAsync(reading);
            LogEvent(Describe(reading, outcome));
            SetStatus(_status.State, _status.Text, reading.ReceivedAt, _status.SamplesReceived + 1);
            if (outcome.Unmatched > 0)
            {
                await LearnChannelsAsync(reading.InstrumentId);
            }
        }
        catch (Exception ex)
        {
            SavePending(reading);
            LogEvent($"Chưa ghi được kết quả mẫu {reading.Barcode} vào CSDL ({ex.Message}) — đã lưu tạm trên máy, sẽ tự ghi lại");
            SetStatus(_status.State, _status.Text, reading.ReceivedAt, _status.SamplesReceived + 1);
        }
    }

    /// <summary>Tự tìm kênh ↔ dịch vụ theo barcode; lỗi ở đây không được ảnh hưởng việc nhận kết quả.</summary>
    private async Task LearnChannelsAsync(Guid instrumentId)
    {
        try
        {
            foreach (var note in await _sink.LearnChannelsAsync(instrumentId))
            {
                LogEvent(note);
            }
        }
        catch (Exception ex)
        {
            LogEvent($"Không tự dò được map kênh: {ex.Message}");
        }
    }

    private static string Describe(InstrumentReading reading, IngestOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.Qc > 0) parts.Add($"{outcome.Qc} QC");
        if (outcome.Stored > 0) parts.Add($"{outcome.Stored} ghi vào kết quả");
        if (outcome.PendingReplacement > 0) parts.Add($"{outcome.PendingReplacement} chờ thay (mẫu đã duyệt)");
        if (outcome.Unmatched > 0) parts.Add($"{outcome.Unmatched} không ghép được (mẫu không có chỉ định đó hoặc kênh chưa map — đã lưu, xem bảng map kênh)");
        var barcode = string.IsNullOrWhiteSpace(reading.Barcode) ? "(không barcode)" : reading.Barcode;
        return $"Nhận kết quả mẫu {barcode}: {reading.Items.Count} kênh → {(parts.Count == 0 ? "không có dòng nào" : string.Join(", ", parts))}";
    }

    private void SavePending(InstrumentReading reading)
    {
        try
        {
            Directory.CreateDirectory(PendingDirectory);
            var file = Path.Combine(PendingDirectory, $"{reading.ReceivedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
            // Ghi ra tệp .tmp rồi đổi tên (nguyên tử trên cùng ổ đĩa) — mất điện giữa chừng không để lại tệp .json dở.
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(reading, JsonOptions), Encoding.UTF8);
            File.Move(temp, file);
        }
        catch (Exception ex)
        {
            LogEvent($"KHÔNG lưu tạm được kết quả mẫu {reading.Barcode}: {ex.Message} — xem lại log thô để nhập tay");
        }

        UpdatePendingCount();
    }

    /// <summary>Gửi bù hàng chờ; vòng tick 30 giây và lệnh "gửi bù ngay" không chạy chồng nhau (tránh ghi trùng một tệp).</summary>
    internal async Task RetryPendingAsync()
    {
        await _retryGate.WaitAsync();
        try
        {
            await RetryPendingCoreAsync();
        }
        finally
        {
            _retryGate.Release();
        }
    }

    private async Task RetryPendingCoreAsync()
    {
        if (!Directory.Exists(PendingDirectory))
        {
            return;
        }

        DeleteStaleTempFiles();
        foreach (var file in Directory.GetFiles(PendingDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            InstrumentReading? reading;
            try
            {
                reading = JsonSerializer.Deserialize<InstrumentReading>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
            }
            catch (Exception ex)
            {
                LogEvent($"Tệp chờ ghi hỏng, đổi tên để xem tay: {Path.GetFileName(file)} ({ex.Message})");
                File.Move(file, file + ".loi", overwrite: true);
                continue;
            }

            if (reading is null)
            {
                File.Delete(file);
                continue;
            }

            try
            {
                var outcome = await _sink.IngestAsync(reading);
                File.Delete(file);
                LogEvent("Đã ghi lại kết quả lưu tạm — " + Describe(reading, outcome));
            }
            catch (Exception)
            {
                break; // CSDL vẫn chưa được — để lần sau.
            }
        }

        UpdatePendingCount();
    }

    /// <summary>Tệp <c>.tmp</c> sót lại do tắt máy giữa lúc ghi: không bao giờ gửi; quá 1 ngày thì xoá.</summary>
    private void DeleteStaleTempFiles()
    {
        try
        {
            foreach (var temp in Directory.GetFiles(PendingDirectory, "*.tmp"))
            {
                if (_clock.GetUtcNow() - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromDays(1))
                {
                    File.Delete(temp);
                }
            }
        }
        catch (Exception)
        {
            // dọn không được thì để lần sau
        }
    }

    private void UpdatePendingCount()
    {
        var count = Directory.Exists(PendingDirectory) ? Directory.GetFiles(PendingDirectory, "*.json").Length : 0;
        if (count != _status.PendingToRetry)
        {
            _status = _status with { PendingToRetry = count };
            _onStatus(_status);
        }
    }

    private void SetStatus(LinkState state, string text, DateTimeOffset? lastResultAt = null, int? samples = null)
    {
        _status = _status with
        {
            State = state,
            Text = text,
            LastResultAt = lastResultAt ?? _status.LastResultAt,
            SamplesReceived = samples ?? _status.SamplesReceived,
        };
        _onStatus(_status);
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetryDelay, _clock, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LogEvent(string note) => SafeLog(new RawLogEntry(_clock.GetLocalNow(), RawLogDirection.Event, _session, [], note));

    private void SafeLog(RawLogEntry entry)
    {
        try
        {
            _rawLogs.Append(_instrument.Id, entry);
        }
        catch (Exception)
        {
            // Đĩa đầy / không có quyền ghi: không được làm dừng nhận kết quả.
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
