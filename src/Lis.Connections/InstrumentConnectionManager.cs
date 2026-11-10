using Lis.Core.Drivers;
using Lis.Core.Instruments;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.Connections;

/// <summary>Kết nối máy đang chạy trên máy trạm này — màn Máy &amp; map đọc trạng thái và gọi nạp lại sau khi lưu.</summary>
public interface IInstrumentConnections
{
    /// <summary>Trạng thái của một máy thay đổi (gọi từ luồng nền).</summary>
    event Action<InstrumentConnectionStatus>? StatusChanged;

    InstrumentConnectionStatus StatusOf(Guid instrumentId);

    /// <summary>Đọc lại danh sách máy: mở kết nối máy mới / đổi cấu hình, đóng máy đã bỏ.</summary>
    Task ReloadAsync();

    /// <summary>Đóng rồi mở lại kết nối một máy (nút "Kết nối lại").</summary>
    Task RestartAsync(Guid instrumentId);

    Task StopAllAsync();
}

/// <summary>
/// Chạy kết nối cho các máy được gán cho máy trạm này (<c>instrument.workstation_code</c> trùng
/// mã máy trạm) — cổng COM chỉ tồn tại trên đúng máy tính cắm cáp, và một máy chỉ được một máy
/// trạm nhận để kết quả không ghi hai lần.
/// </summary>
public sealed class InstrumentConnectionManager(
    Func<Task<IReadOnlyList<InstrumentInfo>>> listInstruments,
    string? workstationCode,
    IRawLogStore rawLogs,
    IReadingSink sink,
    IOrderQueryService queries,
    string pendingRoot,
    TimeProvider clock,
    Func<ConnectionSettings, IByteLink>? linkFactory = null) : IInstrumentConnections
{
    private readonly Dictionary<Guid, (InstrumentRunner Runner, string Signature)> _runners = [];
    private readonly Dictionary<Guid, InstrumentConnectionStatus> _idle = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<ConnectionSettings, IByteLink> _linkFactory = linkFactory ?? ByteLinkFactory.Create;

    /// <summary>Thư mục mặc định cho kết quả chờ ghi lại khi mất CSDL.</summary>
    public static string DefaultPendingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LIS Connect", "cho-ghi");

    public event Action<InstrumentConnectionStatus>? StatusChanged;

    public InstrumentConnectionStatus StatusOf(Guid instrumentId)
    {
        lock (_idle)
        {
            if (_runners.TryGetValue(instrumentId, out var running))
            {
                return running.Runner.Status;
            }

            return _idle.GetValueOrDefault(instrumentId) ?? new InstrumentConnectionStatus(instrumentId, LinkState.NotRunning, "Chưa cấu hình kết nối");
        }
    }

    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var instruments = await listInstruments();
            var wanted = new Dictionary<Guid, (InstrumentInfo Instrument, ConnectionSettings Settings, string Signature)>();
            foreach (var instrument in instruments)
            {
                var reason = WhyNotRunning(instrument, out var settings);
                if (reason is not null)
                {
                    SetIdle(new InstrumentConnectionStatus(instrument.Id, LinkState.NotRunning, reason));
                    continue;
                }

                wanted[instrument.Id] = (instrument, settings!, $"{instrument.ProtocolFamily}|{settings!.ToJson()}");
            }

            foreach (var id in _runners.Keys.ToList())
            {
                if (!wanted.TryGetValue(id, out var want) || want.Signature != _runners[id].Signature)
                {
                    await StopAsync(id);
                }
            }

            foreach (var (id, want) in wanted)
            {
                if (!_runners.ContainsKey(id))
                {
                    StartRunner(want.Instrument, want.Settings, want.Signature);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RestartAsync(Guid instrumentId)
    {
        await _lock.WaitAsync();
        try
        {
            await StopAsync(instrumentId);
        }
        finally
        {
            _lock.Release();
        }

        await ReloadAsync();
    }

    public async Task StopAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var id in _runners.Keys.ToList())
            {
                await StopAsync(id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private string? WhyNotRunning(InstrumentInfo instrument, out ConnectionSettings? settings)
    {
        settings = ConnectionSettings.Parse(instrument.ConnectionJson);
        if (!instrument.Active)
            return "Máy đang tắt (không hoạt động)";
        if (settings.Kind == ConnectionKind.None)
            return "Chưa cấu hình kết nối";
        if (settings.Validate() is { } invalid)
            return $"Cấu hình kết nối chưa đúng: {invalid}";
        if (string.IsNullOrWhiteSpace(instrument.WorkstationCode))
            return "Chưa gán máy trạm — lưu lại máy trên máy tính cắm cáp để gán";
        if (!string.Equals(instrument.WorkstationCode, workstationCode, StringComparison.OrdinalIgnoreCase))
            return $"Chạy ở máy trạm {instrument.WorkstationCode}";
        try
        {
            if (CreateDriver(instrument) is null)
                return $"Chưa có driver cho giao thức {instrument.ProtocolFamily}";
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        return null;
    }

    private void StartRunner(InstrumentInfo instrument, ConnectionSettings settings, string signature)
    {
        var runner = new InstrumentRunner(
            instrument, settings, CreateDriver(instrument)!, _linkFactory, rawLogs, sink, queries, pendingRoot, clock,
            status => StatusChanged?.Invoke(status));
        lock (_idle)
        {
            _idle.Remove(instrument.Id);
            _runners[instrument.Id] = (runner, signature);
        }

        runner.Start();
    }

    private async Task StopAsync(Guid id)
    {
        InstrumentRunner? runner = null;
        lock (_idle)
        {
            if (_runners.Remove(id, out var running))
            {
                runner = running.Runner;
            }
        }

        if (runner is not null)
        {
            await runner.DisposeAsync();
            SetIdle(new InstrumentConnectionStatus(id, LinkState.NotRunning, "Đã đóng kết nối"));
        }
    }

    private static IInstrumentDriver? CreateDriver(InstrumentInfo instrument) =>
        DriverRegistry.Create(instrument.ProtocolFamily, instrument.Model, instrument.DriverConfigJson);

    private void SetIdle(InstrumentConnectionStatus status)
    {
        lock (_idle)
        {
            _idle[status.InstrumentId] = status;
        }

        StatusChanged?.Invoke(status);
    }
}
