using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Projects <see cref="MessageEnriched"/> into <see cref="MessageTag"/> rows.</summary>
internal sealed class MapperlyMessageTagProjector : IBatchProjector<MessageEnriched, MessageTag>
{
    public IEnumerable<MessageTag> Project(MessageEnriched evt) =>
        MessageReadModelMapper.ToMessageTags(evt.MessageSnowflake, evt.Tags);
}
