using System.Globalization;
using System.Text.Json;
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

            // Publish channel metadata so the read-side read_channels table stays current.
            // GuildSyncConsumer owns the authoritative channel list; ChannelSyncConsumer only
            // sees message payloads and has no Name/Type/ParentId context.
            await context.Publish<ChannelChanged>(new
            {
                ChannelId = channel.ChannelId,
                GuildId = guildId,
                Name = channel.Name,
                Topic = (string?)null,
                ChannelType = channel.Type,
                ParentId = channel.ParentId,
                CurrentState = "Active",
                LastUpdatedAt = now,
            }, ct);
        }

        var roles = ExtractRoles(guild.Payload, guildId);

        // GuildChanged allows GuildSagaStateMachine to transition Syncing → Synced and
        // record the metadata snapshot. Also consumed by read-side GuildReadConsumer (Phase 6).
        await context.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = guild.Name,
            Roles = roles,
            CurrentState = "Synced",
            LastUpdatedAt = now,
        }, ct);
    }

    /// <summary>
    /// Extracts role id/name pairs from the guild's raw JSON payload.
    /// Discord always includes roles[] on the full guild object fetched with with_counts=true.
    /// The @everyone role has id == guildId and is excluded — it is not a mention target.
    /// </summary>
    private static IReadOnlyList<GuildRole> ExtractRoles(string guildPayload, long guildId)
    {
        using var doc = JsonDocument.Parse(guildPayload);
        if (!doc.RootElement.TryGetProperty("roles", out var rolesElement) ||
            rolesElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<GuildRole>(rolesElement.GetArrayLength());
        foreach (var role in rolesElement.EnumerateArray())
        {
            if (!role.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                continue;
            if (!long.TryParse(idProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId))
                continue;

            // @everyone: Discord sets its id equal to the guild id; skip it.
            if (roleId == guildId)
                continue;

            var name = role.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString() ?? ""
                : "";

            result.Add(new GuildRole(roleId, name));
        }

        return result;
    }
}
