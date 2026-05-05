using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="GuildChanged"/> events into <see cref="ReadGuild"/> rows,
/// one row per event.
/// </summary>
public sealed class GuildReadConsumer(
    IBatchProjector<GuildChanged, ReadGuild> projector,
    IBulkWriter<ReadGuild> writer,
    ILogger<GuildReadConsumer> logger)
    : ReadModelBatchConsumer<GuildChanged, ReadGuild>(projector, writer, logger);
