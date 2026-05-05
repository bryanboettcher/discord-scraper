using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Projects <see cref="MessageEnriched"/> into a single <see cref="ReadMessage"/> row.
/// Delegates scalar field mapping to <see cref="MessageReadModelMapper.ToReadMessage"/> and
/// fills computed boolean flags and derived text that require IR tree walks.
/// </summary>
internal sealed class MapperlyReadMessageProjector : IBatchProjector<MessageEnriched, ReadMessage>
{
    public IEnumerable<ReadMessage> Project(MessageEnriched evt)
    {
        var ir = evt.IR;
        var msg = MessageReadModelMapper.ToReadMessage(evt);

        msg.PlainText      = IrTextFlattener.Flatten(ir);
        msg.HasCode        = MessageRefExtractor.HasCode(ir.Body);
        msg.HasAttachments = ir.Attachments.Count > 0;
        msg.HasEmbeds      = ir.Embeds.Count > 0;
        // ReplyToId is derived from the IR's ReplyTo context, not a top-level event property.
        msg.ReplyToId      = ir.ReplyTo?.ReplyToMessageId;

        yield return msg;
    }
}
