using System.ComponentModel.DataAnnotations;

namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Denormalized guild context row. Populated by GuildReadConsumer from GuildChanged events.
/// </summary>
public sealed class ReadGuild
{
    [Key]
    public long GuildId { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
