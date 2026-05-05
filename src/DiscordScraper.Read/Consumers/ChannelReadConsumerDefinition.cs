using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

/// <summary>Configures the <see cref="ChannelReadConsumer"/> endpoint with channel-volume-tuned batch settings.</summary>
public sealed class ChannelReadConsumerDefinition(IOptions<ChannelReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<ChannelReadConsumer, ChannelChanged>(options.Value);
