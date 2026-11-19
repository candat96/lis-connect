using Lis.Server.Tenancy;

namespace Lis.Server.AgentApi;

/// <summary>Mỗi 5 giây: lệnh chờ quá 60 giây → <c>expired</c>; lệnh chạy quá hạn → <c>unknown</c>.</summary>
public sealed class AgentCommandSweeper(AgentCommandStore commands, TimeProvider clock, ILogger<AgentCommandSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

    /// <summary>Một vòng quét (test gọi thẳng).</summary>
    public async Task SweepOnceAsync()
    {
        var now = clock.GetUtcNow();
        var expired = await commands.ExpireQueuedAsync(now - AgentChannelHub.QueueTtl, now);
        var timedOut = await commands.TimeoutRunningAsync(now);
        if (expired + timedOut > 0)
        {
            logger.LogInformation("Quét lệnh agent: {Expired} hết hạn, {TimedOut} quá hạn", expired, timedOut);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Quét lệnh agent thất bại");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
