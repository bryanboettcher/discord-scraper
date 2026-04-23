namespace DiscordScraper.Ingestion.Qdrant;

/// <summary>
/// Write access to the Qdrant collection holding embedded Discord messages.
/// The sibling MCP server reads this collection directly; its schema is part
/// of the cross-repo contract, so payload keys here must stay stable.
/// </summary>
public interface IQdrantVectorStore
{
    /// <summary>
    /// Creates the collection if it doesn't exist, configured for the given
    /// vector dimension and cosine distance. Idempotent; safe to call every
    /// worker pass. No-op when the collection is already present.
    /// </summary>
    Task EnsureCollectionAsync(int vectorSize, CancellationToken ct = default);

    /// <summary>
    /// Upserts a single message vector. Uses a deterministic UUID point id so
    /// re-runs overwrite in place rather than duplicating.
    /// </summary>
    Task UpsertAsync(MessageVectorRecord record, CancellationToken ct = default);
}

/// <summary>
/// One message's worth of vector + retrieval metadata. Payload fields become
/// Qdrant-side filter/return keys and are read directly by the MCP server.
/// </summary>
public sealed record MessageVectorRecord(
    string PointId,
    ReadOnlyMemory<float> Vector,
    long MessageId,
    long ChannelId,
    long GuildId,
    long RootChannelId,
    long? ThreadId,
    string AuthorName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> TopicTags,
    string Content);
