using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data;
using DiscordScraper.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Read.Queries;

/// <summary>
/// Batched hydration of RenderContext from read-side entity tables.
///
/// Gaps in v1:
///   UserNames  — no read_users mirror table. Falls back to Fallback strings captured in
///                the IR at projection time (MentionNode.Fallback). Gap tracked as a phase-8
///                follow-up: wire up UserReadConsumer → read_users and extend this loader.
///   RoleNames  — same gap. Role names are already stamped as Fallback at projection time via
///                the ProjectMessageConsumer lookup; no further resolution here.
///   EmojiNames — no emoji table. Custom emoji NameOrGlyph from the IR node is the best we have.
/// </summary>
internal static class RenderContextLoader
{
    /// <summary>
    /// Loads a RenderContext for a single IR. All channel IDs referenced in the IR are resolved
    /// from read_channels in one query. User/role/emoji names fall back to IR-captured strings.
    /// </summary>
    public static Task<RenderContext> LoadAsync(
        MessageIR ir,
        ReadDbContext db,
        CancellationToken ct) =>
        LoadManyAsync([ir], db, ct);

    /// <summary>
    /// Loads a shared RenderContext for a batch of IRs. The channel ID union across all IRs is
    /// resolved in a single <c>WHERE channel_id = ANY(@ids)</c> query.
    /// </summary>
    public static async Task<RenderContext> LoadManyAsync(
        IEnumerable<MessageIR> irs,
        ReadDbContext db,
        CancellationToken ct)
    {
        var channelIds = ExtractChannelIds(irs);

        var channelNames = channelIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.ReadChannels
                .AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);

        return new RenderContext(
            ChannelNames: channelNames,
            RoleNames:    new Dictionary<long, string>(),
            UserNames:    new Dictionary<long, string>(),
            EmojiNames:   new Dictionary<long, string>());
    }

    private static long[] ExtractChannelIds(IEnumerable<MessageIR> irs)
    {
        var ids = new HashSet<long>();
        foreach (var ir in irs)
            CollectChannelIdsFromNodes(ir.Body, ids);
        return ids.Count == 0 ? [] : ids.ToArray();
    }

    private static void CollectChannelIdsFromNodes(IReadOnlyList<MessageNode> nodes, HashSet<long> ids)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case ChannelRefNode c:
                    ids.Add(c.ChannelId);
                    break;
                case FormattingNode f:
                    CollectChannelIdsFromNodes(f.Children, ids);
                    break;
                case QuoteNode q:
                    CollectChannelIdsFromNodes(q.Children, ids);
                    break;
                case LinkNode l:
                    CollectChannelIdsFromNodes(l.DisplayChildren, ids);
                    break;
            }
        }
    }
}
