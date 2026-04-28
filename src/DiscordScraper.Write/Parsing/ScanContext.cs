namespace DiscordScraper.Write.Parsing;

/// <summary>
/// Immutable scanning context threaded through recursive <see cref="ContentScanner.Scan"/> calls.
/// Contains data that cannot be derived from the content string alone.
/// </summary>
internal sealed class ScanContext
{
    /// <summary>
    /// User id → display name, built from the payload's mentions[] array.
    /// Used to populate MentionNode.Fallback for user mentions.
    /// </summary>
    public required IReadOnlyDictionary<long, string> UserMentions { get; init; }

    /// <summary>
    /// Snowflake of the channel this message was fetched from. Null when not provided.
    /// </summary>
    public long? HomeChannelId { get; init; }

    /// <summary>
    /// Display name of the home channel. Null when the caller doesn't have it.
    /// </summary>
    public string? HomeChannelName { get; init; }
}
