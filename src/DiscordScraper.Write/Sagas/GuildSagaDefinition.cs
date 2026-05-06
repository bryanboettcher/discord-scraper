using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Guild;
using MassTransit;
using MassTransit.Middleware;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Partitioning serialises GuildSyncDue and GuildChanged on a single partition lock keyed by
/// GuildId. SyncHeartbeat is left unpartitioned — it carries no GuildId, and MT's CorrelateBy
/// already serialises its fan-out to N matching sagas per the saga state machine's comment.
/// See wiki ADR-001 for rationale.
/// </summary>
public sealed class GuildSagaDefinition : SagaDefinition<GuildSagaState>
{
    public GuildSagaDefinition()
    {
        ConcurrentMessageLimit = 16;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<GuildSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        var partition = new Partitioner(16, new Murmur3UnsafeHashGenerator());

        endpointConfigurator.UsePartitioner<GuildSyncDue>(partition, m => DeterministicGuid.FromSnowflake(m.Message.GuildId));
        endpointConfigurator.UsePartitioner<GuildChanged>(partition, m => DeterministicGuid.FromSnowflake(m.Message.GuildId));

        endpointConfigurator.UseMessageRetry(r =>
            r.Exponential(3,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50)));

        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
