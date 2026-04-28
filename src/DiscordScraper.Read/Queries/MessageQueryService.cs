using DiscordScraper.Core.Graph;
using DiscordScraper.Core.Queries;
using DiscordScraper.Core.Search;
using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Read.Queries;

internal sealed class MessageQueryService(
    IVectorStore vectorStore,
    ISearchService searchService,
    IConversationGraph conversationGraph,
    IDbContextFactory<ReadDbContext> dbFactory,
    IOptions<MessageQueryServiceOptions> options,
    ILogger<MessageQueryService> logger) : IMessageQueryService
{
    public async Task<IReadOnlyList<ScoredMessage>> SearchAsync(MessageSearchQuery query, CancellationToken ct)
    {
        var hasText      = !string.IsNullOrWhiteSpace(query.TextQuery);
        var hasEmbedding = query.Embedding is { Length: > 0 };

        if (!hasText && !hasEmbedding)
        {
            logger.LogDebug("SearchAsync called with neither TextQuery nor Embedding; returning empty");
            return [];
        }

        var opts = options.Value;

        // Run whichever signal(s) are available in parallel when both present.
        Task<SearchResult>? lexicalTask   = null;
        Task<IReadOnlyList<VectorMatch>>? semanticTask = null;

        if (hasText)
        {
            var sq = new SearchQuery(
                Text:      query.TextQuery!,
                GuildId:   query.GuildId,
                ChannelId: query.ChannelId,
                AuthorId:  query.AuthorId,
                After:     query.After,
                Before:    query.Before,
                TopK:      query.TopK);
            lexicalTask = searchService.SearchAsync(sq, ct);
        }

        if (hasEmbedding)
        {
            var filter = new VectorFilter(
                GuildId:   query.GuildId,
                ChannelId: query.ChannelId,
                After:     query.After,
                Before:    query.Before,
                AnyTags:   query.AnyTags);
            semanticTask = vectorStore.SearchAsync(query.Embedding!.Value, filter, query.TopK, ct);
        }

        // Both tasks are already started; awaiting in sequence still runs them concurrently.
        var lexicalScores  = new Dictionary<long, (float Rank, string Snippet)>();
        var semanticScores = new Dictionary<long, float>();

        if (lexicalTask is not null)
        {
            foreach (var hit in (await lexicalTask).Hits)
                lexicalScores[hit.MessageId] = (hit.Rank, hit.Snippet);
        }

        if (semanticTask is not null)
        {
            foreach (var match in await semanticTask)
                semanticScores[match.MessageId] = match.Score;
        }

        // Union the message IDs from both signals
        var allIds = new HashSet<long>(lexicalScores.Keys);
        allIds.UnionWith(semanticScores.Keys);

        if (allIds.Count == 0)
            return [];

        // Hydrate ReadMessages + tags in one go
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var messages = await HydrateMessagesAsync(allIds, db, ct);

        if (messages.Count == 0)
            return [];

        // Build RenderContext from all IRs in one batch
        var ctx = await RenderContextLoader.LoadManyAsync(messages.Select(m => m.Msg.Ir), db, ct);

        // Build channel name lookup for the messages themselves (not just IR refs)
        var channelIds = messages.Select(m => m.Msg.ChannelId).Distinct().ToArray();
        var channelNames = await db.ReadChannels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);

        var results = new List<ScoredMessage>(messages.Count);
        foreach (var (msg, tags) in messages)
        {
            var hasLexical = lexicalScores.TryGetValue(msg.MessageId, out var lex);
            var lexicalScore  = hasLexical ? lex.Rank : 0f;
            var semanticScore = semanticScores.TryGetValue(msg.MessageId, out var s) ? s : 0f;

            var combined = (hasText, hasEmbedding) switch
            {
                (true, true) => opts.LexicalWeight * lexicalScore + opts.SemanticWeight * semanticScore,
                (true, false) => lexicalScore,
                _ => semanticScore,
            };

            var snippet = hasLexical ? lex.Snippet : null;
            var body      = MessageRenderer.Render(msg.Ir, query.OutputFormat, ctx);
            var channel   = channelNames.TryGetValue(msg.ChannelId, out var cn) ? cn : msg.ChannelId.ToString();

            var rendered = new RenderedMessage(
                MessageId:   msg.MessageId,
                ChannelId:   msg.ChannelId,
                GuildId:     msg.GuildId,
                AuthorId:    msg.AuthorId,
                CreatedAt:   msg.CreatedAt,
                EditedAt:    msg.EditedAt,
                Body:        body,
                Tags:        tags,
                AuthorName:  msg.AuthorId.ToString(), // No users table yet — fall back to snowflake
                ChannelName: channel);

            results.Add(new ScoredMessage(rendered, semanticScore, lexicalScore, combined, snippet));
        }

        results.Sort((a, b) => b.CombinedScore.CompareTo(a.CombinedScore));
        return results;
    }

    public async Task<RenderedMessage?> GetMessageAsync(long messageId, RenderFormat format, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var msg = await db.ReadMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageId == messageId, ct);

        if (msg is null)
            return null;

        var tags = await db.MessageTags
            .AsNoTracking()
            .Where(t => t.MessageId == messageId)
            .Select(t => t.Tag)
            .ToListAsync(ct);

        var channelName = await db.ReadChannels
            .AsNoTracking()
            .Where(c => c.ChannelId == msg.ChannelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct) ?? msg.ChannelId.ToString();

        var ctx  = await RenderContextLoader.LoadAsync(msg.Ir, db, ct);
        var body = MessageRenderer.Render(msg.Ir, format, ctx);

        return new RenderedMessage(
            MessageId:   msg.MessageId,
            ChannelId:   msg.ChannelId,
            GuildId:     msg.GuildId,
            AuthorId:    msg.AuthorId,
            CreatedAt:   msg.CreatedAt,
            EditedAt:    msg.EditedAt,
            Body:        body,
            Tags:        tags,
            AuthorName:  msg.AuthorId.ToString(),
            ChannelName: channelName);
    }

    public async Task<RenderedConversation> GetConversationContextAsync(
        long messageId, int radius, RenderFormat format, CancellationToken ct)
    {
        var cluster = await conversationGraph.ExpandConversationAsync(messageId, radius, ct);

        // Center is null when the message is absent from read_messages (excluded or not yet enriched).
        if (cluster.Center is null)
            return new RenderedConversation(null, [], []);

        var allNodeIds = new HashSet<long> { cluster.Center.MessageId };
        foreach (var n in cluster.Ancestors)   allNodeIds.Add(n.MessageId);
        foreach (var n in cluster.Descendants) allNodeIds.Add(n.MessageId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hydrated = await HydrateMessagesAsync(allNodeIds, db, ct);

        var msgLookup = hydrated.ToDictionary(h => h.Msg.MessageId);

        // Shared RenderContext for entire cluster — one channel query
        var ctx = await RenderContextLoader.LoadManyAsync(hydrated.Select(h => h.Msg.Ir), db, ct);

        var channelIds   = hydrated.Select(h => h.Msg.ChannelId).Distinct().ToArray();
        var channelNames = await db.ReadChannels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, ct);

        RenderedMessage Render(long id) =>
            msgLookup.TryGetValue(id, out var h)
                ? RenderHydrated(h, format, ctx, channelNames)
                : FallbackRenderedMessage(id);

        var center      = Render(cluster.Center.MessageId);
        var ancestors   = cluster.Ancestors.Select(n => Render(n.MessageId)).ToList();
        var descendants = cluster.Descendants.Select(n => Render(n.MessageId)).ToList();

        return new RenderedConversation(center, ancestors, descendants);
    }

    // -------------------------------------------------------------------------
    // Hydration helpers
    // -------------------------------------------------------------------------

    private static async Task<List<(ReadMessage Msg, IReadOnlyList<string> Tags)>> HydrateMessagesAsync(
        IEnumerable<long> messageIds,
        ReadDbContext db,
        CancellationToken ct)
    {
        var ids = messageIds.ToArray();

        var messages = await db.ReadMessages
            .AsNoTracking()
            .Where(m => ids.Contains(m.MessageId))
            .ToListAsync(ct);

        if (messages.Count == 0)
            return [];

        var foundIds = messages.Select(m => m.MessageId).ToArray();
        // Materialize first: EF InMemory and some providers can't translate GroupBy unless
        // it is composed into a SQL aggregate. Client-side grouping is correct here.
        var rawTags = await db.MessageTags
            .AsNoTracking()
            .Where(t => foundIds.Contains(t.MessageId))
            .Select(t => new { t.MessageId, t.Tag })
            .ToListAsync(ct);

        var tagMap = rawTags
            .GroupBy(t => t.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => t.Tag).ToList());

        return messages
            .Select(m => (m, tagMap.TryGetValue(m.MessageId, out var tags) ? tags : (IReadOnlyList<string>)[]))
            .ToList();
    }

    private static RenderedMessage RenderHydrated(
        (ReadMessage Msg, IReadOnlyList<string> Tags) hydrated,
        RenderFormat format,
        RenderContext ctx,
        Dictionary<long, string> channelNames)
    {
        var (msg, tags) = hydrated;
        var body    = MessageRenderer.Render(msg.Ir, format, ctx);
        var channel = channelNames.TryGetValue(msg.ChannelId, out var cn) ? cn : msg.ChannelId.ToString();
        return new RenderedMessage(
            MessageId:   msg.MessageId,
            ChannelId:   msg.ChannelId,
            GuildId:     msg.GuildId,
            AuthorId:    msg.AuthorId,
            CreatedAt:   msg.CreatedAt,
            EditedAt:    msg.EditedAt,
            Body:        body,
            Tags:        tags,
            AuthorName:  msg.AuthorId.ToString(),
            ChannelName: channel);
    }

    // Returned when a ConversationNode ID has no corresponding ReadMessage (e.g. message not yet enriched).
    private static RenderedMessage FallbackRenderedMessage(long messageId) =>
        new(messageId, 0L, 0L, 0L, DateTimeOffset.MinValue, null, string.Empty, [], string.Empty, string.Empty);
}
