using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Discord.Options;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>
    /// Bot token from the Discord developer portal. Sent as
    /// <c>Authorization: Bot &lt;token&gt;</c>.
    /// </summary>
    [Required, MinLength(1)]
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Messages per page on the <c>GET /channels/{id}/messages</c> endpoint.
    /// Discord caps this at 100.
    /// </summary>
    [Range(1, 100)]
    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// How long the sync worker waits between full passes after completing a
    /// successful cycle.
    /// </summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// User-Agent header value. Discord ToS requires identifying the library
    /// and version; format is <c>DiscordBot (url, version)</c>.
    /// </summary>
    [Required, MinLength(1)]
    public string UserAgent { get; set; } =
        "DiscordBot (https://github.com/bryanboettcher/discord-scraper, 0.1.0)";
}
