using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>Projects <see cref="MessageEnriched"/> into <see cref="MessageAttachment"/> rows.</summary>
internal sealed class MapperlyMessageAttachmentProjector : IBatchProjector<MessageEnriched, MessageAttachment>
{
    public IEnumerable<MessageAttachment> Project(MessageEnriched evt) =>
        MessageRefExtractor.ExtractAttachments(evt.MessageSnowflake, evt.IR);
}
