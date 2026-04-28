using System.ComponentModel.DataAnnotations;
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Read.Data.Entities;

/// <summary>
/// Read-side projection of a Discord message stored in the read_messages hypertable.
/// MessageId is declared as [Key] for EF tracking purposes; DB-level uniqueness on message_id
/// is logical-only (no physical PK constraint) because TimescaleDB hypertables require
/// created_at in any unique index. Idempotent-insert discipline at the consumer level prevents
/// duplicates — see architecture-plan.md "Read-Side Architecture".
/// </summary>
public sealed class ReadMessage
{
    [Key]
    public long MessageId { get; set; }

    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public long AuthorId { get; set; }

    /// <summary>Hypertable partition key. TimescaleDB chunks by 7-day intervals.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? EditedAt { get; set; }
    public long? ReplyToId { get; set; }

    /// <summary>Typed IR AST stored as jsonb. Mapped via STJ ValueConverter in ReadMessageConfiguration.</summary>
    public MessageIR Ir { get; set; } = null!;

    public string PlainText { get; set; } = string.Empty;

    /// <summary>
    /// TSVECTOR generated column. EF maps it as a computed column via HasComputedColumnSql
    /// so it participates in GIN index declarations but is never written by the application.
    /// </summary>
    public string? Tsv { get; set; }

    public bool HasCode { get; set; }
    public bool HasAttachments { get; set; }
    public bool HasEmbeds { get; set; }
    public bool IsSubstantive { get; set; }
    public bool IsBot { get; set; }
}
