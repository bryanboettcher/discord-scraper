using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data.Entities;

namespace DiscordScraper.Read.Mapping;

/// <summary>
/// Walks a MessageIR depth-first and extracts queryable reference rows.
/// Ordinal values are stable across re-projections of the same IR because the walk order
/// is deterministic (depth-first pre-order, left-to-right). The same IR always produces the
/// same ordinal assignment — critical for idempotent upserts that use (message_id, ordinal) as PK.
/// </summary>
internal static class MessageRefExtractor
{
    /// <summary>
    /// Returns (references, attachments, embeds) extracted from the IR.
    /// References cover: MentionNode (User/Role/Everyone/Here) and ChannelRefNode.
    /// Attachments and embeds come from the IR's top-level lists.
    /// </summary>
    internal static (
        IReadOnlyList<MessageReference> References,
        IReadOnlyList<MessageAttachment> Attachments,
        IReadOnlyList<MessageEmbed> Embeds
    ) Extract(long messageId, MessageIR ir)
    {
        var refs = new List<MessageReference>();
        short ordinal = 0;

        WalkNodes(ir.Body, messageId, refs, ref ordinal);

        var attachments = ir.Attachments
            .Select(a => new MessageAttachment
            {
                MessageId    = messageId,
                AttachmentId = a.Id,
                Url          = a.Url,
                ContentType  = a.ContentType,
                SizeBytes    = a.SizeBytes,
            })
            .ToList();

        var embeds = ir.Embeds
            .Select(e => new MessageEmbed
            {
                MessageId   = messageId,
                EmbedIndex  = (short)e.Index,
                EmbedType   = e.Type,
                Url         = e.Url,
                Title       = e.Title,
                Description = e.Description,
            })
            .ToList();

        return (refs, attachments, embeds);
    }

    private static void WalkNodes(
        IReadOnlyList<MessageNode> nodes,
        long messageId,
        List<MessageReference> refs,
        ref short ordinal)
    {
        foreach (var node in nodes)
            WalkNode(node, messageId, refs, ref ordinal);
    }

    private static void WalkNode(
        MessageNode node,
        long messageId,
        List<MessageReference> refs,
        ref short ordinal)
    {
        switch (node)
        {
            case MentionNode m:
                refs.Add(new MessageReference
                {
                    MessageId = messageId,
                    Kind      = (short)m.Kind,
                    TargetId  = m.Id,
                    Ordinal   = ordinal++,
                });
                break;

            case ChannelRefNode c:
                // ChannelRef uses a Kind value above the MentionKind range to distinguish it
                // from user/role mentions while staying in the same table.
                refs.Add(new MessageReference
                {
                    MessageId = messageId,
                    Kind      = ReferenceKind.ChannelRef,
                    TargetId  = c.ChannelId,
                    Ordinal   = ordinal++,
                });
                break;

            // Recurse into container nodes — FormattingNode, QuoteNode, LinkNode carry children.
            case FormattingNode f:
                WalkNodes(f.Children, messageId, refs, ref ordinal);
                break;

            case QuoteNode q:
                WalkNodes(q.Children, messageId, refs, ref ordinal);
                break;

            case LinkNode l:
                WalkNodes(l.DisplayChildren, messageId, refs, ref ordinal);
                break;

            // TextNode, EmojiNode, CodeBlockNode, InlineCodeNode, TimestampNode produce no reference rows.
        }
    }

    /// <summary>Returns true if the IR body contains any code node (block or inline).</summary>
    internal static bool HasCode(IReadOnlyList<MessageNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is CodeBlockNode or InlineCodeNode) return true;
            if (node is FormattingNode f && HasCode(f.Children)) return true;
            if (node is QuoteNode q && HasCode(q.Children)) return true;
            if (node is LinkNode l && HasCode(l.DisplayChildren)) return true;
        }
        return false;
    }
}

/// <summary>
/// Discriminator values for MessageReference.Kind. MentionKind occupies 0-3; ChannelRef starts at 10
/// to leave room without colliding with future MentionKind additions.
/// </summary>
internal static class ReferenceKind
{
    /// <summary>MessageReference.Kind value for a ChannelRefNode.</summary>
    internal const short ChannelRef = 10;
}
