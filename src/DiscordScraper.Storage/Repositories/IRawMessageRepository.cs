using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Write-side access to the raw Tier 1 capture tables. The sync worker is the
/// only caller; the projection and enrichment workers read from it only
/// indirectly, via the Tier 2 <c>messages</c> table.
/// </summary>
public interface IRawMessageRepository
{
    /// <summary>
    /// Inserts a batch of raw messages. Conflicts on <c>message_id</c> are
    /// ignored because the sync worker may re-observe the same message during
    /// retries or backfills.
    /// </summary>
    /// <returns>Number of rows actually inserted (not counting conflicts).</returns>
    Task<int> InsertAsync(IReadOnlyList<RawMessageEntity> messages, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest <c>message_id</c> currently stored for the given
    /// channel, or <c>null</c> if no rows exist yet. Used as a fallback when
    /// <see cref="RawSyncStateEntity.LastMessageId"/> is missing.
    /// </summary>
    Task<long?> GetMaxMessageIdAsync(long channelId, CancellationToken ct = default);
}
