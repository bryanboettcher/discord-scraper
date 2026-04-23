using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Append-only writer for <c>raw_channels</c> plus read access to the
/// <c>channels_current</c> view. Channels covers both regular channels and
/// threads — same table, different <c>payload-&gt;&gt;'type'</c>.
/// </summary>
public interface IRawChannelRepository
{
    Task<string?> GetCurrentPayloadAsync(long channelId, CancellationToken ct = default);

    Task InsertSnapshotAsync(RawChannelEntity snapshot, CancellationToken ct = default);
}
