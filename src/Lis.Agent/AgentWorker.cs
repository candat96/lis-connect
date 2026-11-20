using System.Text.Json;
using Lis.Connections;
using Lis.Connections.RawMessages;
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

    /// <summary>Hàng chờ bản tin thô trên đĩa (<c>{máy}/{Seq}-{Id}.json</c>), nằm trong <c>cho-gui</c> nên bị cách ly cùng khi đổi phòng khám.</summary>
    public string PendingMessages => Path.Combine(Pending, "ban-tin");

    public string InstrumentsCache => Path.Combine(Root, "instruments.json");

    public string IdentityCache => Path.Combine(Root, "agent.json");

    /// <summary>Kết quả ghép nối (khoá agent đã mã hoá).</summary>
    public string Pairing => Path.Combine(Root, "pairing.json");

    /// <summary>Hàng chờ của phòng khám cũ bị cách ly khi ghép nối sang phòng khám khác — không bao giờ tự gửi đi.</summary>
    public string Quarantine => Path.Combine(Root, "cach-ly");

    /// <summary>Tệp khoá mã hoá dùng khi không có DPAPI (macOS / Linux phát triển).</summary>
    public string DevKey => Path.Combine(Root, "dev.key");

    /// <summary>Agent ghi <c>{version, at}</c> sau khi gửi <c>hello</c> thành công — bản vừa cập nhật dùng để biết mình đã nối lại được server.</summary>
    public string ConnectedMarker => Path.Combine(Root, "ket-noi.json");

    /// <summary>Kết quả lần cập nhật gần nhất (<see cref="Lis.Core.Agents.UpdateReport"/>) — kèm <c>hello</c> rồi xoá.</summary>
    public string UpdateReport => Path.Combine(Root, "cap-nhat.json");
}

/// <summary>
/// Vòng chạy của agent: lấy danh tính + danh sách máy từ server (không được thì dùng bản lưu lần
/// trước — mất mạng lúc bật máy vẫn nối được máy xét nghiệm và giữ kết quả trên đĩa), chạy kết nối
/// máy qua <see cref="InstrumentConnectionManager"/>, định kỳ nạp lại cấu hình và báo trạng thái.
/// </summary>
public sealed class AgentWorker(
    ServerClient server, AgentPaths paths, LogUploader uploader, ILogger<AgentWorker> logger, TimeProvider clock,
    ChannelClient channel, AgentRuntime runtime, RawMessageQueue rawQueue, RawMessageUploader rawUploader) : BackgroundService
{
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);

    /// <summary>Trạng thái máy đổi thì gom bấy nhiêu rồi đẩy lên server (ngoài vòng <see cref="ReloadInterval"/>).</summary>
    public static readonly TimeSpan StatusDebounce = TimeSpan.FromSeconds(1);

    /// <summary>Tên tệp chạy dở của lần cập nhật trước (task cập nhật tạo ra cạnh tệp chạy).</summary>
    public const string StaleUpdateFileName = "LisAgent.new.exe";

    /// <summary>Log thô trên đĩa máy agent giữ bao nhiêu ngày (bản gốc đã đẩy lên server).</summary>
    public const int RawLogKeepDays = Lis.Data.Settings.RawLogDefaults.KeepDays;

    private DateOnly _lastRawLogCleanup;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, InstrumentConnectionStatus> _statuses = [];
    private IReadOnlyList<InstrumentInfo> _instruments = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(paths.Root);
        CleanupStaleUpdateFile(AppContext.BaseDirectory);
        var me = await IdentityAsync(stoppingToken);
        if (me is null)
        {
            return; // đang dừng
        }

        logger.LogInformation("Agent \"{Agent}\" — phòng khám {Clinic}, máy trạm {Workstation}", me.AgentName, me.ClinicCode, me.WorkstationCode);

        var uploading = uploader.RunAsync(stoppingToken);
        if (HasLegacyPending(paths))
        {
            logger.LogWarning(LegacyPendingWarning);
            uploader.AgentNote(LegacyPendingWarning);
        }

        var rawUploading = rawUploader.RunAsync(stoppingToken);
        uploader.AgentNote($"Agent \"{me.AgentName}\" khởi động trên máy {Environment.MachineName}");

        var rawLogs = new UploadingRawLogStore(new RawLogStore(paths.RawLog), uploader);
        var manager = new InstrumentConnectionManager(
            ListInstrumentsAsync, me.WorkstationCode, rawLogs, new RawModeReadingSink(),
            new HttpOrderQueries(server), paths.Pending, clock, rawSink: rawQueue);
        runtime.WorkstationCode = me.WorkstationCode;
        runtime.Manager = manager;
        manager.StatusChanged += status =>
        {
            lock (_statuses)
            {
                _statuses[status.InstrumentId] = status;
            }

            logger.LogInformation("Máy {Instrument}: {State} — {Text}", status.InstrumentId, status.State, status.Text);
            SchedulePush(manager, stoppingToken);
        };

        // Kênh lệnh chạy song song; kênh hỏng / đứt không bao giờ làm dừng vòng chính hay kết nối máy.
        Action onConfigChanged = () => _ = ReloadNowAsync(manager, stoppingToken);
        channel.ConfigChanged += onConfigChanged;
        var channelTask = RunChannelAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (runtime.Revoked)
                {
                    break;
                }

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
            channel.ConfigChanged -= onConfigChanged;
            await manager.StopAllAsync();
            try
            {
                await channelTask;
                await rawUploading;
                await uploading;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Chạy kênh lệnh; mọi lỗi chỉ thành cảnh báo (kênh tự nối lại bên trong), không thoát ra vòng chính.</summary>
    private async Task RunChannelAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await channel.RunAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Kênh lệnh gặp lỗi: {Message}", ex.Message);
                uploader.AgentNote($"Kênh lệnh gặp lỗi: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Web sửa máy → nạp lại ngay (không chờ vòng 30 giây) rồi báo trạng thái.</summary>
    private async Task ReloadNowAsync(InstrumentConnectionManager manager, CancellationToken ct)
    {
        if (runtime.Revoked)
        {
            return;
        }

        try
        {
            await manager.ReloadAsync();
            SchedulePush(manager, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Nạp lại cấu hình theo báo của server lỗi: {Message}", ex.Message);
        }
    }

    private int _pushScheduled;

    /// <summary>Gom các lần đổi trạng thái trong <see cref="StatusDebounce"/> thành một lần đẩy lên server.</summary>
    private void SchedulePush(InstrumentConnectionManager manager, CancellationToken ct)
    {
        if (runtime.Revoked || Interlocked.Exchange(ref _pushScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StatusDebounce, clock, ct);
                Volatile.Write(ref _pushScheduled, 0);
                if (!runtime.Revoked)
                {
                    await ReportStatusAsync(manager, ct);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _pushScheduled, 0);
                if (ex is not OperationCanceledException)
                {
                    logger.LogDebug("Chưa đẩy được trạng thái máy: {Message}", ex.Message);
                }
            }
        }, CancellationToken.None);
    }

    public const string LegacyPendingWarning = "Có kết quả chờ gửi dạng cũ — bản này không gửi được nữa";

    /// <summary>Còn tệp kết quả đã giải mã (dạng của agent cũ) trong <c>cho-gui/{máy}</c>: giữ nguyên, chỉ cảnh báo.</summary>
    internal static bool HasLegacyPending(AgentPaths paths)
    {
        try
        {
            return Directory.Exists(paths.Pending)
                && Directory.EnumerateDirectories(paths.Pending)
                    .Where(d => !string.Equals(Path.GetFileName(d), Path.GetFileName(paths.PendingMessages), StringComparison.OrdinalIgnoreCase))
                    .Any(d => Directory.EnumerateFiles(d, "*.json").Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Xoá <c>LisAgent.new.exe</c> sót lại cạnh tệp chạy sau một lần cập nhật dở; lỗi bỏ qua.</summary>
    internal static void CleanupStaleUpdateFile(string directory)
    {
        try
        {
            File.Delete(Path.Combine(directory, StaleUpdateFileName));
        }
        catch (Exception)
        {
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
            runtime.Instruments = _instruments;
            if (!runtime.Revoked)
            {
                await File.WriteAllTextAsync(paths.InstrumentsCache, JsonSerializer.Serialize(_instruments, Json));
            }
        }
        catch (Exception ex)
        {
            if (_instruments.Count == 0 && TryRead<List<InstrumentInfo>>(paths.InstrumentsCache) is { } cached)
            {
                _instruments = cached;
                runtime.Instruments = _instruments;
            }

            logger.LogWarning("Chưa lấy được danh sách máy từ server ({Message}) — giữ {Count} máy đang biết", ex.Message, _instruments.Count);
        }

        return _instruments;
    }

    private async Task ReportStatusAsync(InstrumentConnectionManager manager, CancellationToken ct)
    {
        var reports = _instruments
            .Select(i => manager.StatusOf(i.Id))
            .Select(s => new InstrumentStatusReport(s.InstrumentId, s.State.ToString(), s.Text, s.LastResultAt, s.SamplesReceived, rawQueue.Count(s.InstrumentId)))
            .ToList();
        await server.SendStatusAsync(reports, ct, OrphanMessageCount());
    }

    /// <summary>Số bản tin trong hàng chờ của các máy không còn gán cho agent này (máy đã chuyển agent / đã xoá).</summary>
    internal int OrphanMessageCount()
    {
        var assigned = _instruments.Select(i => i.Id).ToHashSet();
        var orphans = rawQueue.Instruments().Where(id => !assigned.Contains(id)).ToList();
        var total = orphans.Sum(rawQueue.Count);
        if (total > 0 && clock.GetUtcNow() - _orphanWarnedAt >= OrphanWarnEvery)
        {
            _orphanWarnedAt = clock.GetUtcNow();
            logger.LogWarning(
                "Còn {Count} bản tin của {Machines} máy không còn gán cho agent này — chuyển máy về hoặc liên hệ hỗ trợ", total, orphans.Count);
        }

        return total;
    }

    private static readonly TimeSpan OrphanWarnEvery = TimeSpan.FromMinutes(5);
    private DateTimeOffset _orphanWarnedAt;

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
