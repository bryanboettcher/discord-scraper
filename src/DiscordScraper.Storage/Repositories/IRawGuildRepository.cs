using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Append-only writer for <c>raw_guilds</c> plus read access to the
/// <c>guilds_current</c> view for snapshot-on-change comparisons.
/// </summary>
public interface IRawGuildRepository
{
    /// <summary>Returns the most recent payload for the given guild, or null
    /// if no snapshot has ever been written.</summary>
    Task<string?> GetCurrentPayloadAsync(long guildId, CancellationToken ct = default);

    Task InsertSnapshotAsync(RawGuildEntity snapshot, CancellationToken ct = default);
}
