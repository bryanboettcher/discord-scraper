namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Append-only snapshot log of Discord guilds (servers). A new row is written
/// only when a stable subset of the payload (name, icon, features, etc.)
/// changes; volatile fields like member counts are excluded from the diff.
/// </summary>
public sealed class RawGuildEntity
{
    public long GuildId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
