using MassTransit;

namespace DiscordScraper.MessageEnhancement.Consumers;

/// <summary>
/// Ollama calls are slow (seconds per request); two immediate retries on transient faults give
/// the model a second chance before the saga's 60s timeout fires. No rate limit — Ollama handles
/// concurrent requests from a single node without a queue-side limiter. Outbox is wired at the
/// bus level in Program.cs (UseMongoDbOutbox); EnhanceMessageConsumer only responds.
/// </summary>
public sealed class EnhanceMessageConsumerDefinition : ConsumerDefinition<EnhanceMessageConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<EnhanceMessageConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(2));
    }
}
