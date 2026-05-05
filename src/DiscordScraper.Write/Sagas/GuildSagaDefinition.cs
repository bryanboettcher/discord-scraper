using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// GuildSyncDue uses InsertOnInitial=true. A concurrent re-sync (e.g. admin-triggered while
/// the scheduler fires simultaneously) can produce a Mongo insert race; exponential backoff gives
/// the winning transaction time to commit before the loser retries.
/// </summary>
public sealed class GuildSagaDefinition : SagaDefinition<GuildSagaState>
{
    public GuildSagaDefinition()
    {
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<GuildSagaState> sagaConfigurator,
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
