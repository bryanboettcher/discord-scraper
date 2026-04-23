using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Tracks which Tier 2 messages have been passed through the enrichment
/// pipeline (Ollama tagging + embeddings + Qdrant upsert) and at what version.
/// Bumping <c>Enrichment:Version</c> in config forces a full re-pass without
/// a schema migration.
/// </summary>
public interface IMessageEnrichmentRepository
{
    /// <summary>
    /// Streams messages that either have no enrichment row or whose enrichment
    /// was written at a lower version than <paramref name="currentVersion"/>.
    /// Ordered by <c>message_id</c> so a crashed pass resumes naturally.
    /// </summary>
    IAsyncEnumerable<MessageEntity> EnumerateToEnrichAsync(
        int currentVersion,
        int batchSize,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts a single enrichment row. Uses <c>ON CONFLICT (message_id)
    /// DO UPDATE</c> so re-enrichment overwrites the previous topic tags,
    /// embedding model, and version bookkeeping.
    /// </summary>
    Task UpsertAsync(MessageEnrichmentEntity enrichment, CancellationToken ct = default);
}
