namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Per-channel cursor for delta fetches. The sync worker uses
/// <see cref="LastMessageId"/> to ask Discord for messages strictly newer than
/// the highest snowflake previously seen.
/// </summary>
public sealed class RawSyncStateEntity
{
    public long ChannelId { get; set; }
    public long LastMessageId { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveErrors { get; set; }
}
