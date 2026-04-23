using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Access to Tier 1 <c>raw_messages</c>. Writes come from the sync worker;
/// reads are issued by the projection worker, which streams un-projected rows.
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

    /// <summary>
    /// Streams raw messages that have not yet been projected (i.e. no
    /// corresponding row in <c>messages</c>). Ordered by <c>message_id</c> so
    /// a crash mid-pass resumes naturally on the next run. Consumer controls
    /// batching via <paramref name="batchSize"/>.
    /// </summary>
    IAsyncEnumerable<RawMessageEntity> EnumerateUnprojectedAsync(int batchSize, CancellationToken ct = default);
}
