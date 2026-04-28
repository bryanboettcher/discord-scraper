namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Denormalized embed row extracted from the IR at projection time.
/// No FK constraint back to read_messages (logical relation only).
/// </summary>
public sealed class MessageEmbed
{
    public long MessageId { get; set; }
    public short EmbedIndex { get; set; }
    public string? EmbedType { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
