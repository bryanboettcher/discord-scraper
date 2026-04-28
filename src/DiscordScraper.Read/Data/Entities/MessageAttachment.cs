namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Denormalized attachment row extracted from the IR at projection time.
/// No FK constraint back to read_messages (logical relation only).
/// </summary>
public sealed class MessageAttachment
{
    public long MessageId { get; set; }
    public long AttachmentId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
}
