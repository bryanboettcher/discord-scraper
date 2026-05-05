using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Wraps <see cref="GuildReadModelMapper"/> as an <see cref="IBatchProjector{TEvent,TEntity}"/>.</summary>
internal sealed class MapperlyGuildChangedProjector : IBatchProjector<GuildChanged, ReadGuild>
{
    public IEnumerable<ReadGuild> Project(GuildChanged evt)
    {
        yield return GuildReadModelMapper.ToReadGuild(evt);
    }
}
