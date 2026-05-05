using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects <see cref="ChannelChanged"/> events into <see cref="ReadChannel"/> rows,
/// one row per event.
/// </summary>
public sealed class ChannelReadConsumer(
    IBatchProjector<ChannelChanged, ReadChannel> projector,
    IBulkWriter<ReadChannel> writer,
    ILogger<ChannelReadConsumer> logger)
    : ReadModelBatchConsumer<ChannelChanged, ReadChannel>(projector, writer, logger);
