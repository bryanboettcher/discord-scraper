using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Tier 2 projection writes. The whole table is disposable — a
/// <c>TRUNCATE messages CASCADE</c> followed by a projection-worker pass must
/// reconstruct it from <c>raw_messages</c> alone, so never store anything here
/// that can't be recomputed.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Upserts a batch of projected messages. Uses <c>INSERT ... ON CONFLICT
    /// (message_id) DO UPDATE</c> so re-running projection after logic changes
    /// overwrites rather than erroring.
    /// </summary>
    /// <returns>Rows affected — equals the batch count since every row either
    /// inserts or updates.</returns>
    Task<int> UpsertBatchAsync(IReadOnlyList<MessageEntity> messages, CancellationToken ct = default);
}
