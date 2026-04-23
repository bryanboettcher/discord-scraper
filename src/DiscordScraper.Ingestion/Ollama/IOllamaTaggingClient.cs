namespace DiscordScraper.Ingestion.Ollama;

/// <summary>
/// Classifies a message's content via a local LLM. Returns 1–3 topic tags plus
/// a substantiveness flag the enrichment worker uses to decide whether the
/// message is worth embedding + indexing at all.
/// </summary>
public interface IOllamaTaggingClient
{
    string Model { get; }

    Task<TagResult> TagAsync(string content, CancellationToken ct = default);
}

/// <param name="TopicTags">1–3 short lowercase keywords, may be empty if the
/// model returned nothing parseable.</param>
/// <param name="IsSubstantive">False for greetings, single-word replies,
/// pure emoji, or bare links — the enrichment worker skips embedding +
/// Qdrant upsert when this is false.</param>
public sealed record TagResult(IReadOnlyList<string> TopicTags, bool IsSubstantive);
