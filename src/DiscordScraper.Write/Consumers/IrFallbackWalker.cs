using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Pure static helpers for collecting unresolved fallback IDs from a parsed IR and
/// populating them after the repo lookups complete.
///
/// Two-pass design (collect then populate) keeps ProjectMessageConsumer readable and
/// avoids threading repo calls through the recursive walk.
/// </summary>
internal static class IrFallbackWalker
{
    public static (IReadOnlyCollection<long> ChannelIds, IReadOnlyCollection<long> RoleIds)
        CollectMissingFallbacks(MessageIR ir)
    {
        var channelIds = new HashSet<long>();
        var roleIds = new HashSet<long>();

        foreach (var node in ir.Body)
            CollectFromNode(node, channelIds, roleIds);

        return (channelIds, roleIds);
    }

    public static MessageIR PopulateFallbacks(
        MessageIR ir,
        IReadOnlyDictionary<long, string> channelNames,
        IReadOnlyDictionary<long, string> roleNames)
    {
        var newBody = PopulateNodes(ir.Body, channelNames, roleNames);

        // Body is the only collection that can contain nodes with fallbacks.
        // Attachments, Embeds, and ReplyContext are structural metadata, not content refs.
        if (ReferenceEquals(newBody, ir.Body))
            return ir;

        return ir with { Body = newBody };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Collect pass
    // ──────────────────────────────────────────────────────────────────────────

    private static void CollectFromNode(MessageNode node, HashSet<long> channelIds, HashSet<long> roleIds)
    {
        switch (node)
        {
            case ChannelRefNode { Fallback: null } chan:
                channelIds.Add(chan.ChannelId);
                break;

            case MentionNode { Kind: MentionKind.Role, Fallback: null } mention when mention.Id.HasValue:
                roleIds.Add(mention.Id.Value);
                break;

            case FormattingNode fmt:
                foreach (var child in fmt.Children)
                    CollectFromNode(child, channelIds, roleIds);
                break;

            case QuoteNode quote:
                foreach (var child in quote.Children)
                    CollectFromNode(child, channelIds, roleIds);
                break;

            case LinkNode link:
                foreach (var child in link.DisplayChildren)
                    CollectFromNode(child, channelIds, roleIds);
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Populate pass
    // ──────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<MessageNode> PopulateNodes(
        IReadOnlyList<MessageNode> nodes,
        IReadOnlyDictionary<long, string> channelNames,
        IReadOnlyDictionary<long, string> roleNames)
    {
        List<MessageNode>? result = null;

        for (var i = 0; i < nodes.Count; i++)
        {
            var original = nodes[i];
            var updated = PopulateNode(original, channelNames, roleNames);

            if (!ReferenceEquals(updated, original))
            {
                // Lazy-copy: only allocate a new list when something actually changed.
                if (result is null)
                {
                    result = new List<MessageNode>(nodes.Count);
                    for (var j = 0; j < i; j++)
                        result.Add(nodes[j]);
                }
            }

            result?.Add(updated);
        }

        return result is not null ? result : nodes;
    }

    private static MessageNode PopulateNode(
        MessageNode node,
        IReadOnlyDictionary<long, string> channelNames,
        IReadOnlyDictionary<long, string> roleNames)
    {
        switch (node)
        {
            case ChannelRefNode { Fallback: null } chan:
                var chanName = channelNames.TryGetValue(chan.ChannelId, out var cn)
                    ? cn
                    : "<unknown channel>";
                return chan with { Fallback = chanName };

            case MentionNode { Kind: MentionKind.Role, Fallback: null } mention when mention.Id.HasValue:
                var roleName = roleNames.TryGetValue(mention.Id.Value, out var rn)
                    ? rn
                    : "<unknown role>";
                return mention with { Fallback = roleName };

            case FormattingNode fmt:
                var newFmtChildren = PopulateNodes(fmt.Children, channelNames, roleNames);
                return ReferenceEquals(newFmtChildren, fmt.Children)
                    ? fmt
                    : fmt with { Children = newFmtChildren };

            case QuoteNode quote:
                var newQuoteChildren = PopulateNodes(quote.Children, channelNames, roleNames);
                return ReferenceEquals(newQuoteChildren, quote.Children)
                    ? quote
                    : quote with { Children = newQuoteChildren };

            case LinkNode link:
                var newLinkChildren = PopulateNodes(link.DisplayChildren, channelNames, roleNames);
                return ReferenceEquals(newLinkChildren, link.DisplayChildren)
                    ? link
                    : link with { DisplayChildren = newLinkChildren };

            default:
                return node;
        }
    }
}
