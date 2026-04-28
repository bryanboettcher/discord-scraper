using MassTransit;

namespace DiscordScraper.MessageEnhancement.Consumers;

/// <summary>
/// Immediate(2) handles transient pgvector write blips; Interval(3, 2s) covers Postgres
/// connection pool exhaustion or brief node unavailability without hammering the DB during
/// recovery. Outbox is wired at the bus level; IndexMessageConsumer only responds.
/// </summary>
public sealed class IndexMessageConsumerDefinition : ConsumerDefinition<IndexMessageConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<IndexMessageConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(2);
            r.Interval(3, TimeSpan.FromSeconds(2));
        });
    }
}
