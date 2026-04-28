using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// PrefetchCount=500 / ConcurrentMessageLimit=100 is sized for backfill bursts where
/// ChannelSyncConsumer fan-outs MessageCaptured at Discord's pagination rate across many
/// channels in parallel. Tune down after the initial crawl if memory pressure appears.
/// </summary>
public sealed class MessageSagaDefinition : SagaDefinition<MessageSagaState>
{
    public MessageSagaDefinition()
    {
        ConcurrentMessageLimit = 100;
    }

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<MessageSagaState> sagaConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.PrefetchCount = 500;

        // Mongo OCC raises MongoDbConcurrencyException on version collision; retry picks up fresh state.
        endpointConfigurator.UseMessageRetry(r => r.Immediate(3));

        // Outbox ensures MessageStateChanged publishes are durable across saga retries.
        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
