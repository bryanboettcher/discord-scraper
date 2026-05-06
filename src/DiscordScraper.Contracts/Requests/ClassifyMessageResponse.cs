namespace DiscordScraper.Contracts.Requests;

/// <summary>
/// Response from the Classify phase (LLM topic tagging + vector store indexing).
/// IndexedAt is set by the consumer after writing to the vector store.
///
/// IsBot / IsSubstantive / DetectedLanguage are deliberately NOT carried here — the
/// AnalyzeMessage phase produces them and the saga writes them to its state at that
/// transition. By the time Classify runs, those facts are settled on the saga.
///
/// Init-only properties — positional ctors break ctx.Init&lt;T&gt; with anonymous objects.
/// </summary>
public sealed record ClassifyMessageResponse : IMeasured
{
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string ClassifyModelVersion { get; init; } = string.Empty;
    public DateTimeOffset IndexedAt { get; init; }

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
