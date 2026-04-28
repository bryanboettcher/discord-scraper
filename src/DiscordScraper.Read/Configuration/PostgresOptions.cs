using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Read.Configuration;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    [Required, MinLength(1)]
    public string ConnectionString { get; set; } = string.Empty;
}
