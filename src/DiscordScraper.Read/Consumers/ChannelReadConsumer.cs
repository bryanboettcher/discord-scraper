using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

public sealed class ChannelReadConsumer(
    IDbContextFactory<ReadDbContext> factory,
    IReadBulkWriter writer,
    ILogger<ChannelReadConsumer> logger)
    : ReadModelBatchConsumer<ChannelChanged>(factory, writer, logger)
{
    protected override IEnumerable<object> Project(ChannelChanged evt)
    {
        yield return ChannelReadModelMapper.ToReadChannel(evt);
    }
}
