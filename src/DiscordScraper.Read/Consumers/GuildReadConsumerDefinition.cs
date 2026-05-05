using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Consumers;

/// <summary>Configures the <see cref="GuildReadConsumer"/> endpoint with guild-volume-tuned batch settings.</summary>
public sealed class GuildReadConsumerDefinition(IOptions<GuildReadBatchOptions> options)
    : ReadModelBatchConsumerDefinition<GuildReadConsumer, GuildChanged>(options.Value);
