using DiscordScraper.Contracts.Events.Guild;
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
        var guilds = options.Value.Guilds;

        logger.LogInformation("Sync tick: scheduling {GuildCount} guild(s)", guilds.Count);

        await using var scope = scopeFactory.CreateAsyncScope();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        foreach (var rawId in guilds)
        {
            if (!long.TryParse(rawId, out var guildId))
            {
                logger.LogError("Configured guild ID {RawId} is not a valid snowflake; skipping", rawId);
                continue;
            }

            await publish.Publish<GuildSyncRequested>(
                new { GuildId = guildId, CurrentState = "Syncing" },
                ct);

            logger.LogDebug("Published GuildSyncRequested for guild {GuildId}", guildId);
        }
    }
}
