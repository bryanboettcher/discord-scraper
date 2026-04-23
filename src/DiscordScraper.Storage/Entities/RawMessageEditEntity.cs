namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Append-only log of observed Discord message edits. One row per distinct
/// <c>(message_id, edited_at)</c> tuple. <see cref="Payload"/> is the full
/// edited message payload returned by the REST API.
/// </summary>
public sealed class RawMessageEditEntity
{
    public long MessageId { get; set; }
    public DateTimeOffset EditedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
