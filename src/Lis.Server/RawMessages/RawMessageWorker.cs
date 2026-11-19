using Lis.Server.Tenancy;

namespace Lis.Server.RawMessages;

/// <summary>
/// Việc nền của bản tin thô, cho mọi phòng khám: lúc khởi động và mỗi 60 giây phục hồi bản tin giải mã dở rồi giải mã các bản tin
/// <c>cho-giai-ma</c> còn sót (<see cref="Lis.Data.RawMessages.RawMessageProcessor.RecoverAsync"/>); mỗi giờ xoá bản tin đã xong quá 90 ngày.
/// Một phòng khám lỗi không chặn phòng khám khác.
/// </summary>
public sealed class RawMessageWorker(
    MasterStore master, TenantRegistry tenants, ClinicProvisioner provisioner, TimeProvider clock, ILogger<RawMessageWorker> logger) : BackgroundService
{
    public static readonly TimeSpan RecoverPeriod = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PurgePeriod = TimeSpan.FromHours(1);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Phục hồi + giải mã bản tin còn dở của mọi phòng khám (test gọi thẳng).</summary>
    public Task RecoverAllAsync(CancellationToken ct) =>
        ForEachClinicAsync("phục hồi bản tin thô", async (clinic, tenant) =>
        {
            var recovery = await tenant.RawProcessor.RecoverAsync(ct);
            if (recovery.Total > 0)
            {
                logger.LogInformation("Phòng khám {Clinic}: phục hồi {Requeued} bản tin giải mã lại, {NeedsReview} bản tin cần xem lại",
                    clinic.Code, recovery.Requeued, recovery.NeedsReview);
            }
        }, ct);

    /// <summary>Xoá bản tin đã xong lưu quá <see cref="Retention"/> của mọi phòng khám (test gọi thẳng).</summary>
    public Task PurgeAllAsync(CancellationToken ct) =>
        ForEachClinicAsync("dọn bản tin thô", async (clinic, tenant) =>
        {
            var deleted = await tenant.RawMessages.DeleteOlderThanAsync(clock.GetUtcNow() - Retention);
            if (deleted > 0)
            {
                logger.LogInformation("Phòng khám {Clinic}: đã xoá {Count} bản tin thô quá {Days} ngày", clinic.Code, deleted, Retention.Days);
            }
        }, ct);

    private async Task ForEachClinicAsync(string what, Func<Clinic, TenantServices, Task> action, CancellationToken ct)
    {
        foreach (var clinic in await master.ClinicsAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (provisioner.FailedUpgrades.ContainsKey(clinic.Id))
            {
                continue;
            }

            try
            {
                await action(clinic, tenants.For(clinic));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Phòng khám {Clinic}: lỗi khi {What}", clinic.Code, what);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var lastPurge = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(RecoverPeriod, clock);
            // Lượt đầu chạy ngay lúc khởi động (phục hồi đúng một lần), các lượt sau mỗi RecoverPeriod.
            do
            {
                if (clock.GetUtcNow() - lastPurge >= PurgePeriod)
                {
                    lastPurge = clock.GetUtcNow();
                    await PurgeSafelyAsync(stoppingToken);
                }

                await RecoverSafelyAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RecoverSafelyAsync(CancellationToken ct)
    {
        try
        {
            await RecoverAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Vòng phục hồi bản tin thô thất bại");
        }
    }

    private async Task PurgeSafelyAsync(CancellationToken ct)
    {
        try
        {
            await PurgeAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Vòng dọn bản tin thô thất bại");
        }
    }
}
