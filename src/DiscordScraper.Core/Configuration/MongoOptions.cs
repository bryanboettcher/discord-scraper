using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Core.Configuration;

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    [Required, MinLength(1)]
    public string ConnectionString { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string DatabaseName { get; set; } = "discord_scraper";
}
