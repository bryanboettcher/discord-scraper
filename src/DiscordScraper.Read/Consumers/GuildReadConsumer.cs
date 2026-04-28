using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

internal sealed class GuildReadConsumer(
    IDbContextFactory<ReadDbContext> factory,
    IReadBulkWriter writer,
    ILogger<GuildReadConsumer> logger)
    : ReadModelBatchConsumer<GuildChanged>(factory, writer, logger)
{
    protected override IEnumerable<object> Project(GuildChanged evt)
    {
        yield return GuildReadModelMapper.ToReadGuild(evt);
    }
}
