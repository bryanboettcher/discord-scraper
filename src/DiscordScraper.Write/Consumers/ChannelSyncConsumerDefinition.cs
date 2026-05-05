using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Channel;
using MassTransit;
using MassTransit.Middleware;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Endpoint configuration for ChannelSyncConsumer.
/// </summary>
/// <remarks>
/// Partitioning: 16 virtual partitions keyed on ChannelId route same-channel syncs to the same
/// partition; combined with ConcurrentMessageLimit=1 this serialises per-channel processing.
/// True per-channel queues are unnecessary because SyncSchedulerService fires at most one
/// ChannelSyncDue per channel per tick. <para/>
///
/// Rate limiting: Discord's global bot rate limit is 50 req/s; the limiter is per-endpoint per-process,
/// so divide across pods when scaling. <para/>
///
/// Retry: 2 attempts with 2s interval handles transient Discord 5xx. Longer faults (429, 403) flow
/// to dead-letter for operator inspection rather than retrying indefinitely.
/// </remarks>
public sealed class ChannelSyncConsumerDefinition : ConsumerDefinition<ChannelSyncConsumer>
{
    public ChannelSyncConsumerDefinition()
    {
        ConcurrentMessageLimit = 1;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ChannelSyncConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // PrefetchCount > ConcurrentMessageLimit so the partition filter always has messages
        // queued; 5 is conservative for the current single-pod deployment.
        endpointConfigurator.PrefetchCount = 5;

        var partition = new Partitioner(16, new Murmur3UnsafeHashGenerator());
        endpointConfigurator.UsePartitioner<ChannelSyncDue>(
            partition,
            m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));

        endpointConfigurator.UseRateLimit(50, TimeSpan.FromSeconds(1));
        endpointConfigurator.UseMessageRetry(r => r.Interval(2, TimeSpan.FromSeconds(2)));
    }
}
