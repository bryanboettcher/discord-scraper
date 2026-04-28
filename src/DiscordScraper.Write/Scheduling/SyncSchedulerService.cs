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
    internal async Task PublishOnceAsync(CancellationToken ct)
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
}
