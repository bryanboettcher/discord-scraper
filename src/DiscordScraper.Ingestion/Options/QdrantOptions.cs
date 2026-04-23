using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Ingestion.Options;

public sealed class QdrantOptions
{
    public const string SectionName = "Qdrant";

    [Required, MinLength(1)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string CollectionName { get; set; } = "discord-messages";
}
