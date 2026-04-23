using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Storage.Options;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    [Required, MinLength(1)]
    public string ConnectionString { get; set; } = string.Empty;
}
