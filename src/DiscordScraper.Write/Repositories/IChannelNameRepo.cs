namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Direct point-fetch against the channel_sagas Mongo collection for display-name resolution.
/// Used by <c>ProjectMessageConsumer</c> to populate <c>ChannelRefNode.Fallback</c> at projection
/// time. No caching layer — indexed $in queries are sub-millisecond.
/// </summary>
public interface IChannelNameRepo
{
    /// <summary>
    /// Returns an id → name map for the requested channel IDs. Missing IDs are absent from the
    /// result; callers substitute their own default fallback string.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetNamesAsync(
        IReadOnlyCollection<long> channelIds,
        CancellationToken ct);
}
