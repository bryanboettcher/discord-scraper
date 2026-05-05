using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Enrichment.Ollama.Options;

/// <summary>
/// HTTP identity for the Classify (LLM) Ollama client. Concurrency, prefetch, kill-switch,
/// HTTP/saga timeouts live on EnrichmentClassifyOptions (Contracts.Configuration) — this class
/// is intentionally minimal so a model swap doesn't drag along middleware tunables.
/// </summary>
public sealed class OllamaClassifyOptions
{
    public const string SectionName = "Ollama:Classify";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Model { get; set; } = "llama3.1:8b";
}
