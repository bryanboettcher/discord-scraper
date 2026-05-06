using DiscordScraper.Read.Configuration;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

/// <summary>Configures the <see cref="ChannelReadConsumer"/> endpoint with channel-volume-tuned batch settings.</summary>
public sealed class ChannelReadConsumerDefinition(IOptions<ChannelReadBatchOptions> options)
    : ConsumerDefinition<ChannelReadConsumer>
{
    private readonly ChannelReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ChannelReadConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}
