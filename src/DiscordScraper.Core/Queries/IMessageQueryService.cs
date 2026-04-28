namespace DiscordScraper.Core.Queries;

/// <summary>
/// Composite query: ranks messages by combined semantic + lexical relevance.
/// Composes IVectorStore, ISearchService, IConversationGraph, and the renderer.
/// </summary>
public interface IMessageQueryService
{
    /// <summary>
    /// Hybrid search combining vector (semantic) and TSV (lexical) results.
    /// Score blending is controlled by MessageQueryServiceOptions.
    /// Returns results ordered by CombinedScore descending.
    /// </summary>
    Task<IReadOnlyList<ScoredMessage>> SearchAsync(MessageSearchQuery query, CancellationToken ct);

    /// <summary>Hydrates and renders a single message by Discord snowflake.</summary>
    Task<RenderedMessage?> GetMessageAsync(long messageId, RenderFormat format, CancellationToken ct);

    /// <summary>
    /// Returns the conversation surrounding the given message: ancestors + center + descendants.
    /// Delegates to IConversationGraph.ExpandConversationAsync, then hydrates each node.
    /// </summary>
    Task<RenderedConversation> GetConversationContextAsync(long messageId, int radius, RenderFormat format, CancellationToken ct);
}
