namespace DiscordScraper.Core.Queries;

/// <summary>
/// Configures score blending for MessageQueryService hybrid search.
/// Weights apply when both lexical and semantic results are present.
/// When only one signal is available the single-signal score is used directly as CombinedScore.
/// </summary>
public sealed class MessageQueryServiceOptions
{
    public const string SectionName = "MessageQueryService";

    /// <summary>Weight applied to the TSV rank from ISearchService. Default: 0.5.</summary>
    public float LexicalWeight { get; init; } = 0.5f;

    /// <summary>Weight applied to the cosine similarity score from IVectorStore. Default: 0.5.</summary>
    public float SemanticWeight { get; init; } = 0.5f;
}
