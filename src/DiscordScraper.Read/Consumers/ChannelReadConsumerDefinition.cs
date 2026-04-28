using DiscordScraper.Contracts.Events.Channel;

namespace DiscordScraper.Read.Consumers;

internal sealed class ChannelReadConsumerDefinition
    : ReadModelBatchConsumerDefinition<ChannelReadConsumer, ChannelChanged>;
