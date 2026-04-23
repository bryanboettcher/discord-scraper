using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Ingestion.Options;

public sealed class EnrichmentOptions
{
    public const string SectionName = "Enrichment";

    /// <summary>
    /// Bumping this forces the enrichment worker to re-process every message
    /// whose row in <c>message_enrichments</c> was written at a lower version.
    /// Use when the tagging prompt, embedding model, or output shape changes.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int Version { get; set; } = 1;

    /// <summary>Messages pulled from the backlog per round-trip.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>Delay between passes when the backlog is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum content length sent to the tagging LLM. Anything longer gets
    /// truncated to keep latency bounded and context windows sane. Embedding
    /// can handle much more, but tag prompts don't need the full message.
    /// </summary>
    [Range(100, 32000)]
    public int MaxTaggingContentChars { get; set; } = 2000;
}
