namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Extracted reference row for a message. Covers reply-to, mentions, channel refs —
/// kind discriminates the reference type using the MentionKind enum ordinal.
/// No FK constraint back to read_messages (logical relation only; see architecture plan).
/// </summary>
public sealed class MessageReference
{
    public long MessageId { get; set; }

    /// <summary>Discriminator — maps to MentionKind or a reference-type enum defined by the consumer.</summary>
    public short Kind { get; set; }

    public long? TargetId { get; set; }

    /// <summary>Position within this message's reference list. Part of composite PK.</summary>
    public short Ordinal { get; set; }
}
