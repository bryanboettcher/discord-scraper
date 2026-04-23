namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Read-only projection of <c>channels_current</c> view: latest snapshot per
/// channel. Mirrors <see cref="GuildCurrentView"/>.
/// </summary>
public sealed class ChannelCurrentView
{
    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
