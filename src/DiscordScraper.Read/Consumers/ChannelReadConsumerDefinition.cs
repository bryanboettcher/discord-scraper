using DiscordScraper.Contracts.Events.Channel;

namespace DiscordScraper.Read.Consumers;

public sealed class ChannelReadConsumerDefinition
    : ReadModelBatchConsumerDefinition<ChannelReadConsumer, ChannelChanged>;
