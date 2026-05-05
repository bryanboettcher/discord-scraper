using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// ChannelSyncDue uses InsertOnInitial=true. During the initial guild backfill,
/// GuildSyncConsumer fans out ChannelSyncDue for every channel simultaneously; if the
/// scheduler fires a second request before the first insert commits, Mongo raises a DuplicateKey
/// conflict. Exponential backoff clears the collision window before faulting.
/// </summary>
public sealed class ChannelSagaDefinition : SagaDefinition<ChannelSagaState>
{
    public ChannelSagaDefinition()
    {
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<ChannelSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        // Same exponential shape as MessageSagaDefinition — mirrors MT's JobSagaDefinition.
        endpointConfigurator.UseMessageRetry(r =>
            r.Exponential(8,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(100)));

        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
