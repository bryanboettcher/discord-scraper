using MassTransit;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Each consume issues 3 Discord REST calls; the per-endpoint limiter (10 req/s) keeps the
/// process well below the global bot limit (50 req/s). Outbox makes the ChannelSyncRequested
/// publishes atomic with the consume ack.
/// </summary>
internal sealed class GuildSyncConsumerDefinition : ConsumerDefinition<GuildSyncConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<GuildSyncConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(3));
        endpointConfigurator.UseRateLimit(10, TimeSpan.FromSeconds(1));
        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
