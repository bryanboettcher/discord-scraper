namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// One row per (message, tag) pair. Tags come from EnhanceMessageConsumer's Ollama tagging step.
/// No FK constraint back to read_messages (logical relation only).
/// </summary>
public sealed class MessageTag
{
    public long MessageId { get; set; }
    public string Tag { get; set; } = string.Empty;
}
