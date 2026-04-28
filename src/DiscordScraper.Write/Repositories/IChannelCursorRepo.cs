namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Batch-fetches sync cursors from channel_sagas so GuildSyncConsumer can stamp
/// CursorSnowflake on each ChannelSyncRequested without a per-channel Mongo round-trip.
/// </summary>
public interface IChannelCursorRepo
{
    /// <summary>
    /// Returns a channel-id → last-synced-snowflake map for the requested IDs.
    /// Channels that have no saga entry yet are absent from the result; callers use 0 as the default.
    /// </summary>
    Task<IReadOnlyDictionary<long, long>> GetCursorsAsync(
        IReadOnlyCollection<long> channelIds,
        CancellationToken ct);
}
