using DiscordScraper.Read.Configuration;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

/// <summary>Configures the <see cref="GuildReadConsumer"/> endpoint with guild-volume-tuned batch settings.</summary>
public sealed class GuildReadConsumerDefinition(IOptions<GuildReadBatchOptions> options)
    : ConsumerDefinition<GuildReadConsumer>
{
    private readonly GuildReadBatchOptions _opts = options.Value;

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<GuildReadConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.ConfigureReadBatchConsumer(consumerConfigurator, context, _opts);
}
