using MassTransit;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Discord global bot rate limit (50 req/s) is shared with ChannelSyncConsumer; the limit is
/// applied per-endpoint per-process. Divide by pod count when scaling out. UseMongoDbOutbox
/// makes publishes inside Consume() transactional even though PinPollConsumer is stateless.
/// </summary>
internal sealed class PinPollConsumerDefinition : ConsumerDefinition<PinPollConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PinPollConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(2));
        endpointConfigurator.UseRateLimit(50, TimeSpan.FromSeconds(1));
        endpointConfigurator.UseMongoDbOutbox(context);
    }
}
