using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Cursor state for the Discord sync worker. One row per channel; the worker
/// advances <see cref="RawSyncStateEntity.LastMessageId"/> after every
/// successful batch so subsequent polls only fetch new messages.
/// </summary>
public interface ISyncStateRepository
{
    Task<RawSyncStateEntity?> GetAsync(long channelId, CancellationToken ct = default);

    Task UpsertAsync(RawSyncStateEntity state, CancellationToken ct = default);

    Task RecordErrorAsync(long channelId, string error, CancellationToken ct = default);
}
