using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Projects <see cref="MessageEnriched"/> into <see cref="MessageReference"/> rows.</summary>
internal sealed class MapperlyMessageReferenceProjector : IBatchProjector<MessageEnriched, MessageReference>
{
    public IEnumerable<MessageReference> Project(MessageEnriched evt) =>
        MessageRefExtractor.ExtractReferences(evt.MessageSnowflake, evt.IR);
}
