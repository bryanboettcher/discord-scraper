using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// PrefetchCount=500 / ConcurrentMessageLimit=8 is sized for backfill bursts where
/// ChannelSyncConsumer fan-outs MessageCaptured at Discord's pagination rate across many
/// channels in parallel. ConcurrentMessageLimit=8 (down from 100) reduces the rate of
/// same-correlation Mongo insert races: with InsertOnInitial=true, two concurrent
/// MessageCaptured for the same snowflake both attempt InsertOneAsync; the loser catches
/// DuplicateKey and retries the full saga pipeline — lower concurrency means fewer simultaneous
/// collisions on the same document, so the exponential backoff has room to succeed.
/// </summary>
public sealed class MessageSagaDefinition : SagaDefinition<MessageSagaState>
{
    public MessageSagaDefinition()
    {
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<MessageSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.PrefetchCount = 500;

        // Exponential backoff on Mongo OCC / DuplicateKey: initial insert races from
        // InsertOnInitial=true cause MongoDbConcurrencyException. The losing consumer must
        // wait for the winning transaction to commit before its find-and-update can succeed.
        // Mongo's default txn timeout is ~60s; 8 attempts spanning ~2s worst-case window is
        // sufficient for the commit to clear. Mirrors MT's own JobSagaDefinition shape.
        endpointConfigurator.UseMessageRetry(r =>
            r.Exponential(8,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(100)));

        // Outbox ensures MessageStateChanged publishes are durable across saga retries.
        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
