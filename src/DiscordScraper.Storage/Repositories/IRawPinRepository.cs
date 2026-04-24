using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Writer for <c>raw_pins</c> plus read access to <c>pins_current</c>. The
/// sync worker uses this to snapshot-on-change the set of pinned messages per
/// channel — append a row only when the pinned message IDs drift.
/// </summary>
public interface IRawPinRepository
{
    Task<string?> GetCurrentPayloadAsync(long channelId, CancellationToken ct = default);

    Task InsertSnapshotAsync(RawPinEntity snapshot, CancellationToken ct = default);
}
