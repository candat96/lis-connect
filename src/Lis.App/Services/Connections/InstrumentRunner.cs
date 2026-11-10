using System.Text;
using System.Text.Json;
using Lis.Core.Drivers;
using Lis.Core.Ingest;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.App.Services.Connections;

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
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private IByteLink? _link;
    private string _session = "";
    private DateTimeOffset _nextPendingRetry;
    private InstrumentConnectionStatus _status;

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
        Action<InstrumentConnectionStatus> onStatus)
    {
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

        _stop.Dispose();
        _gate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
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
            await WithGateAsync(() => { _driver.Reset(); return Task.CompletedTask; });
            LogEvent($"Đã kết nối · {link.Description}");
            SetStatus(LinkState.Connected, $"Đã kết nối · {link.Description}");

            using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var ticker = TickLoopAsync(sessionStop.Token);
            try
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var read = await link.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        LogEvent("Máy đóng kết nối");
                        break;
                    }

                    var bytes = buffer.AsSpan(0, read).ToArray();
                    SafeLog(new RawLogEntry(_clock.GetLocalNow(), RawLogDirection.In, _session, bytes));
                    await WithGateAsync(() => HandleAsync(_driver.Receive(bytes, _clock.GetLocalNow()), ct));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogEvent($"Mất kết nối: {ex.Message}");
            }
            finally
            {
                _link = null;
                await sessionStop.CancelAsync();
                try
                {
                    await ticker;
                }
                catch (OperationCanceledException)
                {
                }
            }

            SetStatus(LinkState.Error, $"Mất kết nối — thử lại sau {RetryDelay.TotalSeconds:0} giây");
            await DelayAsync(ct);
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await WithGateAsync(() => HandleAsync(_driver.Tick(_clock.GetLocalNow()), ct));
            if (_clock.GetUtcNow() >= _nextPendingRetry)
            {
                _nextPendingRetry = _clock.GetUtcNow() + PendingRetryInterval;
                await RetryPendingAsync();
            }
        }
    }

    private async Task WithGateAsync(Func<Task> work)
    {
        await _gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
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

        foreach (var reading in output.Readings)
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
            File.WriteAllText(file, JsonSerializer.Serialize(reading, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogEvent($"KHÔNG lưu tạm được kết quả mẫu {reading.Barcode}: {ex.Message} — xem lại log thô để nhập tay");
        }

        UpdatePendingCount();
    }

    internal async Task RetryPendingAsync()
    {
        if (!Directory.Exists(PendingDirectory))
        {
            return;
        }

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
