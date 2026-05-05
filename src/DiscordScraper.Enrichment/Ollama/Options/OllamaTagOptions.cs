using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Enrichment.Ollama.Options;

/// <summary>
/// HTTP identity for the Tag (embedding) Ollama client. Concurrency, prefetch, kill-switch,
/// retry, and HTTP/saga timeouts live on EnrichmentTagOptions (Contracts.Configuration) —
/// this class is intentionally minimal so a model swap or endpoint move doesn't drag along
/// middleware tunables.
/// </summary>
public sealed class OllamaTagOptions
{
    public const string SectionName = "Ollama:Tag";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Vector dimension the model produces. Used when lazily creating the
    /// vector store collection. nomic-embed-text = 768.
    /// </summary>
    [Range(1, 8192)]
    public int VectorSize { get; set; } = 768;
}
