using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Discord;
using DiscordScraper.Write.Repositories;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Fetches guild metadata + channel list from Discord, publishes ChannelSyncRequested
/// for each text-like channel, then publishes GuildChanged so the saga can settle.
///
/// Separated from GuildSagaStateMachine deliberately: the saga tracks lifecycle state;
/// this consumer does the I/O. Using pub/sub (not request/response) because there is
/// nothing to respond to — fan-out is the shape of this step.
/// </summary>
internal sealed class GuildSyncConsumer(
    IDiscordClient discord,
    IChannelCursorRepo cursorRepo,
    ISystemClock clock,
    ILogger<GuildSyncConsumer> logger) : IConsumer<GuildSyncRequested>
{
    // Discord channel types we treat as message-bearing for v1.
    // 0=GUILD_TEXT, 5=GUILD_ANNOUNCEMENT, 10=ANNOUNCEMENT_THREAD, 11=PUBLIC_THREAD, 12=PRIVATE_THREAD
    private static readonly HashSet<int> TextLikeChannelTypes = [0, 5, 10, 11, 12];

    public async Task Consume(ConsumeContext<GuildSyncRequested> context)
    {
        var guildId = context.Message.GuildId;
        var ct = context.CancellationToken;

        var guild = await discord.GetGuildAsync(guildId.ToString(), ct);
        var channels = await discord.GetGuildChannelsAsync(guildId.ToString(), ct);
        var threads = await discord.GetGuildActiveThreadsAsync(guildId.ToString(), ct);

        var allChannels = channels
            .Concat(threads)
            .Where(c => TextLikeChannelTypes.Contains(c.Type))
            .ToList();

        logger.LogInformation(
            "Guild {GuildId} ({GuildName}): found {ChannelCount} text-like channels/threads",
            guildId, guild.Name, allChannels.Count);

        // Batch-fetch existing cursors so each ChannelSyncRequested resumes from its last
        // high-water mark. Channels with no saga entry yet get cursor=0 (full history scan).
        var channelIds = allChannels.Select(c => c.ChannelId).ToList();
        var cursors = await cursorRepo.GetCursorsAsync(channelIds, ct);

        var now = clock.UtcNow;
        foreach (var channel in allChannels)
        {
            var cursor = cursors.TryGetValue(channel.ChannelId, out var c) ? c : 0L;

            await context.Publish<ChannelSyncRequested>(new
            {
                ChannelId = channel.ChannelId,
                GuildId = guildId,
                CursorSnowflake = (long?)cursor,
                CurrentState = "Requested",
                LastUpdatedAt = now,
            }, ct);
        }

        // GuildChanged allows GuildSagaStateMachine to transition Syncing → Synced and
        // record the metadata snapshot. Also consumed by read-side GuildReadConsumer (Phase 6).
        await context.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = guild.Name,
            CurrentState = "Synced",
            LastUpdatedAt = now,
        }, ct);
    }
}
