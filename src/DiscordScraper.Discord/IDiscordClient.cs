using DiscordScraper.Discord.Models;

namespace DiscordScraper.Discord;

/// <summary>
/// Read-only Discord REST access. All methods are idempotent; all returned
/// records carry the original JSON payload alongside the decoded fields needed
/// for routing, so callers can persist the payload verbatim.
/// </summary>
public interface IDiscordClient
{
    /// <summary>
    /// Returns the guilds the bot's user is a member of. Equivalent to
    /// <c>GET /users/@me/guilds</c>. Discord caps this at 200 per page; we do
    /// not paginate because a single bot is unlikely to join that many.
    /// </summary>
    Task<IReadOnlyList<DiscordGuildRaw>> GetCurrentUserGuildsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the full guild object for the given snowflake.
    /// <c>GET /guilds/{id}?with_counts=true</c>.
    /// </summary>
    Task<DiscordGuildRaw> GetGuildAsync(string guildId, CancellationToken ct = default);

    /// <summary>
    /// Returns all non-thread channels in the guild. Threads are retrieved via
    /// <see cref="GetGuildActiveThreadsAsync"/> and
    /// <see cref="EnumerateArchivedThreadsAsync"/>.
    /// <c>GET /guilds/{id}/channels</c>.
    /// </summary>
    Task<IReadOnlyList<DiscordChannelRaw>> GetGuildChannelsAsync(string guildId, CancellationToken ct = default);

    /// <summary>
    /// Returns currently-active (non-archived) threads across all channels in
    /// the guild. <c>GET /guilds/{id}/threads/active</c>.
    /// </summary>
    Task<IReadOnlyList<DiscordChannelRaw>> GetGuildActiveThreadsAsync(string guildId, CancellationToken ct = default);

    /// <summary>
    /// Yields archived public threads under a channel, oldest-first. Paginated
    /// by <c>before</c> archive timestamp; the client handles paging internally.
    /// <c>GET /channels/{id}/threads/archived/public</c>.
    /// </summary>
    IAsyncEnumerable<DiscordChannelRaw> EnumerateArchivedThreadsAsync(string channelId, CancellationToken ct = default);

    /// <summary>
    /// Yields messages newer than <paramref name="afterSnowflake"/> from the
    /// given channel, paginating through the API until exhausted. Pass
    /// <c>afterSnowflake=0</c> for a full backfill.
    /// <c>GET /channels/{id}/messages?after=&amp;limit=</c>.
    /// </summary>
    /// <param name="guildId">
    /// Stamped onto each yielded message since Discord does not include it in
    /// the per-channel message response.
    /// </param>
    IAsyncEnumerable<DiscordMessageRaw> EnumerateChannelMessagesAsync(
        string channelId,
        long guildId,
        long afterSnowflake,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current pinned messages for a channel or thread. Response
    /// is capped at 50 per Discord's server-side pin limit. Each returned
    /// <see cref="DiscordMessageRaw"/> carries its <c>edited_timestamp</c> so
    /// the sync worker can detect edits without re-fetching full histories.
    /// <c>GET /channels/{id}/pins</c>.
    /// </summary>
    Task<DiscordChannelPins> GetChannelPinsAsync(string channelId, long guildId, CancellationToken ct = default);
}
