using NpgsqlTypes;

namespace DiscordScraper.Storage.Entities;

/// <summary>
/// Tier 2 projection of a Discord message. Derived entirely from
/// <see cref="RawMessageEntity"/> and current channel/guild snapshots. This
/// table is disposable: <c>TRUNCATE messages CASCADE</c> followed by a
/// projection-worker run must always rebuild it successfully.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReplyToId"/> and <see cref="ThreadId"/> are signals for
/// downstream grouping, never retrieval filters. A channel view must include
/// every message whose <see cref="ChannelId"/> matches, regardless of thread
/// structure.
/// </para>
/// </remarks>
public sealed class MessageEntity
{
    public long MessageId { get; set; }
    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public long AuthorId { get; set; }
    public required string AuthorName { get; set; }
    public bool AuthorIsBot { get; set; }
    public required string Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public long? ReplyToId { get; set; }
    public long? ThreadId { get; set; }
    public long RootChannelId { get; set; }
    public bool HasAttachments { get; set; }

    /// <summary>
    /// Generated-always-stored tsvector column populated by Postgres. Exposed
    /// here so EF migrations create the column; never set from C#.
    /// </summary>
    public NpgsqlTsVector? ContentTsv { get; set; }

    public MessageEnrichmentEntity? Enrichment { get; set; }
}
