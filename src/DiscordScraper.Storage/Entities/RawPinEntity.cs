namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Append-only snapshot log of a channel's current pinned-message list. A new
/// row is written only when the set of pinned message IDs changes (pin added,
/// pin removed). Payload is Discord's raw JSON array response so downstream
/// can inspect the full pinned message objects, not just the IDs.
/// </summary>
public sealed class RawPinEntity
{
    public long ChannelId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
