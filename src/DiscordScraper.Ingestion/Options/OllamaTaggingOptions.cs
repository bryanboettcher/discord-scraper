using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Ingestion.Options;

public sealed class OllamaTaggingOptions
{
    public const string SectionName = "Ollama:Tagging";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Model { get; set; } = "qwen2.5-coder:7b";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;
}
