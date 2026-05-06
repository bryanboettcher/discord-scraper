using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Channel;
using MassTransit;
using MassTransit.Middleware;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Partitioning serialises events for the same ChannelId on a single partition lock,
/// preventing same-saga InsertOnInitial races and Mongo WriteConflict on concurrent
/// FindOneAndReplace. See wiki ADR-001 (saga endpoint partitioning) for rationale.
/// </summary>
public sealed class ChannelSagaDefinition : SagaDefinition<ChannelSagaState>
{
    public ChannelSagaDefinition()
    {
        ConcurrentMessageLimit = 16;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<ChannelSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        // Single shared Partitioner instance — all UsePartitioner calls below use the same
        // lock pool so events for the same ChannelId hash to the same partition slot
        // regardless of message type.
        var partition = new Partitioner(16, new Murmur3UnsafeHashGenerator());

        endpointConfigurator.UsePartitioner<ChannelSyncDue>(partition, m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));
        endpointConfigurator.UsePartitioner<ChannelSyncCompleted>(partition, m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));
        endpointConfigurator.UsePartitioner<ChannelChanged>(partition, m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));
        endpointConfigurator.UsePartitioner<PinSetChanged>(partition, m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));
        endpointConfigurator.UsePartitioner<PinPollDue>(partition, m => DeterministicGuid.FromSnowflake(m.Message.ChannelId));

        // Retry as rare-event safety net — partitioning prevents the bulk of contention.
        endpointConfigurator.UseMessageRetry(r =>
            r.Exponential(3,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50)));

        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
