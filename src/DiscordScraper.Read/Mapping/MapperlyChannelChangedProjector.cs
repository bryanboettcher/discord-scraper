using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Wraps <see cref="ChannelReadModelMapper"/> as an <see cref="IBatchProjector{TEvent,TEntity}"/>.</summary>
internal sealed class MapperlyChannelChangedProjector : IBatchProjector<ChannelChanged, ReadChannel>
{
    public IEnumerable<ReadChannel> Project(ChannelChanged evt)
    {
        yield return ChannelReadModelMapper.ToReadChannel(evt);
    }
}
