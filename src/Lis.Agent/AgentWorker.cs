using System.Text.Json;
using Lis.Connections;
using Lis.Data.Instruments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>Thư mục dữ liệu của agent: log thô, kết quả chờ gửi, danh sách máy lần cuối lấy được.</summary>
public sealed record AgentPaths(string Root)
{
    public static AgentPaths Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LIS Agent"));

    public string RawLog => Path.Combine(Root, "rawlog");

    public string Pending => Path.Combine(Root, "cho-gui");

    public string InstrumentsCache => Path.Combine(Root, "instruments.json");

    public string IdentityCache => Path.Combine(Root, "agent.json");
}

/// <summary>
/// Vòng chạy của agent: lấy danh tính + danh sách máy từ server (không được thì dùng bản lưu lần
/// trước — mất mạng lúc bật máy vẫn nối được máy xét nghiệm và giữ kết quả trên đĩa), chạy kết nối
/// máy qua <see cref="InstrumentConnectionManager"/>, định kỳ nạp lại cấu hình và báo trạng thái.
/// </summary>
public sealed class AgentWorker(
    ServerClient server, AgentPaths paths, LogUploader uploader, ILogger<AgentWorker> logger, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);

    /// <summary>Log thô trên đĩa máy agent giữ bao nhiêu ngày (bản gốc đã đẩy lên server).</summary>
    public const int RawLogKeepDays = Lis.Data.Settings.RawLogDefaults.KeepDays;

    private DateOnly _lastRawLogCleanup;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, InstrumentConnectionStatus> _statuses = [];
    private IReadOnlyList<InstrumentInfo> _instruments = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(paths.Root);
        var me = await IdentityAsync(stoppingToken);
        if (me is null)
        {
            return; // đang dừng
        }

        logger.LogInformation("Agent \"{Agent}\" — phòng khám {Clinic}, máy trạm {Workstation}", me.AgentName, me.ClinicCode, me.WorkstationCode);

        var uploading = uploader.RunAsync(stoppingToken);
        uploader.AgentNote($"Agent \"{me.AgentName}\" khởi động trên máy {Environment.MachineName}");

        var rawLogs = new UploadingRawLogStore(new RawLogStore(paths.RawLog), uploader);
        var manager = new InstrumentConnectionManager(
            ListInstrumentsAsync, me.WorkstationCode, rawLogs, new HttpReadingSink(server),
            new HttpOrderQueries(server), paths.Pending, clock);
        manager.StatusChanged += status =>
        {
            lock (_statuses)
            {
                _statuses[status.InstrumentId] = status;
            }

            logger.LogInformation("Máy {Instrument}: {State} — {Text}", status.InstrumentId, status.State, status.Text);
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    CleanupRawLogs(rawLogs);
                    await manager.ReloadAsync();
                    await ReportStatusAsync(manager, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Chưa làm mới được với server: {Message}", ex.Message);
                    uploader.AgentNote($"Chưa làm mới được với server: {ex.Message}");
                }

                await Task.Delay(ReloadInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await manager.StopAllAsync();
            try
            {
                await uploading;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Dọn log thô cũ trên đĩa, mỗi ngày một lần — agent chạy liên tục nhiều tháng, không dọn thì đầy đĩa.</summary>
    private void CleanupRawLogs(UploadingRawLogStore rawLogs)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (today == _lastRawLogCleanup)
        {
            return;
        }

        _lastRawLogCleanup = today;
        try
        {
            var deleted = rawLogs.DeleteOlderThan(today, RawLogKeepDays);
            if (deleted > 0)
            {
                logger.LogInformation("Đã xóa {Count} tệp log thô cũ hơn {Days} ngày", deleted, RawLogKeepDays);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Dọn log thô cũ lỗi: {Message}", ex.Message);
        }
    }

    /// <summary>Danh tính từ server; server chưa tới được thì dùng bản lưu; chưa có bản lưu thì chờ và thử lại.</summary>
    private async Task<AgentMe?> IdentityAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var me = await server.MeAsync(ct);
                await File.WriteAllTextAsync(paths.IdentityCache, JsonSerializer.Serialize(me, Json), ct);
                return me;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (TryRead<AgentMe>(paths.IdentityCache) is { } cached)
                {
                    logger.LogWarning("Chưa tới được server ({Message}) — chạy với cấu hình đã lưu", ex.Message);
                    return cached;
                }

                logger.LogError("Chưa tới được server và chưa có cấu hình đã lưu: {Message}. Thử lại sau 10 giây.", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<InstrumentInfo>> ListInstrumentsAsync()
    {
        try
        {
            _instruments = await server.InstrumentsAsync();
            await File.WriteAllTextAsync(paths.InstrumentsCache, JsonSerializer.Serialize(_instruments, Json));
        }
        catch (Exception ex)
        {
            if (_instruments.Count == 0 && TryRead<List<InstrumentInfo>>(paths.InstrumentsCache) is { } cached)
            {
                _instruments = cached;
            }

            logger.LogWarning("Chưa lấy được danh sách máy từ server ({Message}) — giữ {Count} máy đang biết", ex.Message, _instruments.Count);
        }

        return _instruments;
    }

    private async Task ReportStatusAsync(InstrumentConnectionManager manager, CancellationToken ct)
    {
        var reports = _instruments
            .Select(i => manager.StatusOf(i.Id))
            .Select(s => new InstrumentStatusReport(s.InstrumentId, s.State.ToString(), s.Text, s.LastResultAt, s.SamplesReceived, s.PendingToRetry))
            .ToList();
        await server.SendStatusAsync(reports, ct);
    }

    private static T? TryRead<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
