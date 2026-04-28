using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Batch consumer that projects MessageStateChanged events into the message read tables
/// (read_messages, message_references, message_attachments, message_embeds, message_tags) in a
/// single transaction per batch.
/// </summary>
/// <remarks>
/// Subscription is on the base interface so the consumer sees every state-change subtype, but
/// only <see cref="MessageEnriched"/> produces entities. Intermediate states (Projected, Enhanced,
/// Indexed) and the Excluded terminal carry incomplete data and are silently skipped — projecting
/// them would expose partial reads and churn upserts.
/// </remarks>
internal sealed class MessageReadConsumer(
    IDbContextFactory<ReadDbContext> factory,
    IReadBulkWriter writer,
    ILogger<MessageReadConsumer> logger)
    : ReadModelBatchConsumer<MessageStateChanged>(factory, writer, logger)
{
    protected override IEnumerable<object> Project(MessageStateChanged evt) => evt switch
    {
        MessageEnriched enriched => ProjectFromEnriched(enriched),
        _ => [],
    };

    private static IEnumerable<object> ProjectFromEnriched(MessageEnriched evt)
    {
        var snowflake = evt.MessageSnowflake;
        var ir = evt.IR;

        var msg = MessageReadModelMapper.ToReadMessage(evt);
        msg.PlainText      = IrTextFlattener.Flatten(ir);
        msg.HasCode        = MessageRefExtractor.HasCode(ir.Body);
        msg.HasAttachments = ir.Attachments.Count > 0;
        msg.HasEmbeds      = ir.Embeds.Count > 0;
        // ReplyToId is derived from the IR's ReplyTo context, not a top-level event property.
        msg.ReplyToId      = ir.ReplyTo?.ReplyToMessageId;

        yield return msg;

        var (refs, attachments, embeds) = MessageRefExtractor.Extract(snowflake, ir);

        foreach (var r in refs)        yield return r;
        foreach (var a in attachments) yield return a;
        foreach (var e in embeds)      yield return e;

        foreach (var tag in MessageReadModelMapper.ToMessageTags(snowflake, evt.Tags))
            yield return tag;
    }
}
