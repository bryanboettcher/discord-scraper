namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Read-only projection of <c>guilds_current</c> view: latest snapshot per
/// guild derived via <c>SELECT DISTINCT ON (guild_id) ... ORDER BY guild_id,
/// fetched_at DESC</c>. Used by the sync worker to compare incoming payloads
/// against the last persisted version before writing a new snapshot row.
/// </summary>
public sealed class GuildCurrentView
{
    public long GuildId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
