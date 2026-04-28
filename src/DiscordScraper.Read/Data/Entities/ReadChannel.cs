using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Denormalized channel context row. Populated by ChannelReadConsumer from ChannelChanged events.
/// Used by the renderer to resolve channel names at query time without joining saga store.
/// </summary>
public sealed class ReadChannel
{
    [Key]
    public long ChannelId { get; set; }

    public long GuildId { get; set; }
    public string Name { get; set; } = string.Empty;
    public short Type { get; set; }
    public long? ParentId { get; set; }
    public string? Topic { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
