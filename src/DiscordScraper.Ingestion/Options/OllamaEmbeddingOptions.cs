using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Ingestion.Options;

public sealed class OllamaEmbeddingOptions
{
    public const string SectionName = "Ollama:Embedding";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Vector dimension the model produces. Used when lazily creating the
    /// Qdrant collection. nomic-embed-text = 768.
    /// </summary>
    [Range(1, 8192)]
    public int VectorSize { get; set; } = 768;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;
}
