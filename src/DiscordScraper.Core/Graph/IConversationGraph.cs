namespace DiscordScraper.Core.Graph;

public interface IConversationGraph
{
    /// <summary>Returns descendants of rootMessageId via reply_to chains, oldest-first, up to maxDepth levels.</summary>
    Task<IReadOnlyList<ConversationNode>> GetThreadAsync(long rootMessageId, int maxDepth, CancellationToken ct);

    /// <summary>Walks reply_to upward from messageId, returning ancestors newest-last.</summary>
    Task<IReadOnlyList<ConversationNode>> GetReplyAncestorsAsync(long messageId, int maxDepth, CancellationToken ct);

    /// <summary>Expands a conversation: ancestors up to radius + descendants up to radius from centerMessageId.</summary>
    Task<ConversationCluster> ExpandConversationAsync(long centerMessageId, int radius, CancellationToken ct);
}

public sealed record ConversationNode(
    long MessageId,
    long? ReplyToId,
    long ChannelId,
    long GuildId,
    long AuthorId,
    DateTimeOffset CreatedAt,
    int Depth);

/// <summary>
/// Center is null when the requested centerMessageId is not present in read_messages
/// (message excluded, or not yet enriched). Callers must distinguish this from a real
/// but isolated message (which has a non-null Center with empty Ancestors and Descendants).
/// </summary>
public sealed record ConversationCluster(
    ConversationNode? Center,
    IReadOnlyList<ConversationNode> Ancestors,
    IReadOnlyList<ConversationNode> Descendants);
