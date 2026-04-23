namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Immutable raw capture of a Discord message. <see cref="Payload"/> holds the
/// untouched Discord REST response as jsonb. Everything else is either the
/// primary key (<see cref="MessageId"/>) or a decoded-once column used for
/// indexing and cursor maintenance.
/// </summary>
public sealed class RawMessageEntity
{
    public long MessageId { get; set; }
    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Payload { get; set; }
}
