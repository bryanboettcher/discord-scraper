namespace DiscordScraper.Contracts.Requests;

/// <summary>
/// Response from the Tag phase (embedding via nomic-embed-text).
/// The embedding vector is stored on the saga and forwarded to ClassifyRequest.
/// Init-only properties — positional ctors break ctx.Init&lt;T&gt; with anonymous objects.
/// </summary>
public sealed record TagMessageResponse : IStampable
{
    public IReadOnlyList<float> Embedding { get; init; } = [];
    public string EmbeddingModelVersion { get; init; } = string.Empty;
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }
}
