using DiscordScraper.Contracts.Events.Guild;

namespace DiscordScraper.Read.Consumers;

internal sealed class GuildReadConsumerDefinition
    : ReadModelBatchConsumerDefinition<GuildReadConsumer, GuildChanged>;
