using DiscordScraper.Contracts.Events.Guild;

namespace DiscordScraper.Read.Consumers;

public sealed class GuildReadConsumerDefinition
    : ReadModelBatchConsumerDefinition<GuildReadConsumer, GuildChanged>;
