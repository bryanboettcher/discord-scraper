namespace DiscordScraper.Core.Queries;

/// <summary>
/// Read-side queries scoped to channels. Backs the MCP "what's active / what's recent" tools.
/// </summary>
public interface IChannelQueryService
{
    /// <summary>
    /// Returns channels with activity in the last sinceHours hours, ordered by message count descending.
    /// Queries the messages_per_channel_per_hour continuous aggregate.
    /// </summary>
    Task<IReadOnlyList<ChannelSummary>> GetActiveChannelsAsync(long guildId, int sinceHours, CancellationToken ct);

    /// <summary>
    /// Returns the most recent count messages in a channel, newest-first, rendered at the requested format.
    /// </summary>
    Task<IReadOnlyList<RenderedMessage>> GetRecentMessagesAsync(long channelId, int count, RenderFormat format, CancellationToken ct);
}
