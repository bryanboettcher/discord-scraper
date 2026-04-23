namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Append-only snapshot log of Discord channels. Includes threads (Discord
/// channel types 10/11/12). A new row is written only when a stable subset
/// (name, topic, parent_id, archived, nsfw) changes.
/// </summary>
public sealed class RawChannelEntity
{
    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
