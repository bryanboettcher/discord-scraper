using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Discord.Options;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Write.Scheduling;

internal sealed class SyncSchedulerService(
    IServiceScopeFactory scopeFactory,
    IOptions<DiscordOptions> options,
    ISystemClock clock,
    ILogger<SyncSchedulerService> logger) : BackgroundService
{
    // Lets the MT bus finish topology setup before the first publish.
    private static readonly TimeSpan WarmUpDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(WarmUpDelay, stoppingToken);

        var ticker = new PeriodicTimer(options.Value.SyncInterval);

        // Publish immediately after warm-up, then on each subsequent tick.
        do
        {
            await PublishOnceAsync(stoppingToken);
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken));
    }

    // Internal so tests can exercise per-tick logic without fighting PeriodicTimer.
    //
    // Failure semantics: a thrown exception propagates out, the BackgroundService faults, and the
    // default StopHost behaviour terminates the process. That is intentional — when the outbox
    // store (Mongo) is unreachable, the entire ingester is non-functional, and exiting non-zero
    // lets the orchestrator restart-loop surface the failure. Swallowing here would silently
    // stall the heartbeat and leave guild sagas un-resynced, which is strictly worse. We catch
    // only to log the reason at Error level *before* the host shutdown sequence eats the stack
    // trace, then rethrow.
    internal async Task PublishOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            var now = clock.UtcNow;
            var staleAfter = now - options.Value.SyncInterval;

            await publish.Publish<SyncHeartbeat>(new
            {
                Timestamp = now,
                StaleAfter = staleAfter,
            }, ct);

            logger.LogDebug(
                "SyncHeartbeat published at {Timestamp}, StaleAfter {StaleAfter}",
                now,
                staleAfter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SyncHeartbeat publish failed — ingester will shut down");
            throw;
        }
    }
}
