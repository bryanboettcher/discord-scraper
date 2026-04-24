namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Read-only projection of <c>pins_current</c>: the latest pin snapshot per
/// channel. Lets the sync worker compare the incoming pin set against what's
/// already persisted before deciding to write a new snapshot row.
/// </summary>
public sealed class PinCurrentView
{
    public long ChannelId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
