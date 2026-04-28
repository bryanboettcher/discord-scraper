using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Ingester.Options;

internal sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required, MinLength(1)]
    public string Host { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Username { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string Password { get; set; } = string.Empty;
}
