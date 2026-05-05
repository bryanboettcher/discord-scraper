using System.Globalization;
using System.Net;
using System.Text.Json;
using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Discord;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Paginates Discord messages for a channel and publishes MessageCaptured per message,
/// then publishes ChannelSyncCompleted to advance the saga cursor.
/// </summary>
/// <remarks>
/// Cursor: <see cref="ChannelSyncDue.CursorSnowflake"/> carries the last-known high-water
/// mark, stamped by the saga when re-entering Syncing. Stateless consumer; no Mongo read at consume.
/// Per-channel ordering: the consumer definition uses a Partitioner keyed on ChannelId combined
/// with ConcurrentMessageLimit=1 so syncs for the same channel run serially.
/// </remarks>
public sealed class ChannelSyncConsumer(
    IDiscordClient discord,
    ISystemClock clock,
    ILogger<ChannelSyncConsumer> logger) : IConsumer<ChannelSyncDue>
{
    // Discord caps the messages-after response at 100. A short page signals caught-up.
    private const int PageSize = 100;

    private static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true };

    public async Task Consume(ConsumeContext<ChannelSyncDue> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["ChannelId"] = msg.ChannelId,
            ["GuildId"] = msg.GuildId,
        });

        var cursor = msg.CursorSnowflake ?? 0L;
        var highestSnowflake = cursor;
        var messageCount = 0;

        try
        {
            await foreach (var raw in discord.EnumerateChannelMessagesAsync(
                               msg.ChannelId.ToString(), msg.GuildId, cursor, ct))
            {
                if (raw.MessageId > highestSnowflake)
                    highestSnowflake = raw.MessageId;

                var (authorId, authorIsBot) = ParseAuthor(raw.Payload);

                // ChannelSyncDue does not carry the channel display name — leave HomeChannelName
                // null. ProjectMessageConsumer resolves it from the saga repo for cross-channel refs.
                await context.Publish<MessageCaptured>(new
                {
                    MessageId = DeterministicGuid.FromSnowflake(raw.MessageId),
                    MessageSnowflake = raw.MessageId,
                    ChannelId = raw.ChannelId,
                    GuildId = raw.GuildId,
                    AuthorId = authorId,
                    CurrentState = "Captured",
                    UpdatedOn = raw.CreatedAt,
                    PayloadJson = raw.Payload,
                    AuthorIsBot = authorIsBot,
                    HomeChannelName = (string?)null,
                }, ct);

                messageCount++;
            }
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            logger.LogWarning(
                "Channel {ChannelId}: Discord returned {StatusCode} — marking not present",
                msg.ChannelId, ex.StatusCode);

            await context.Publish<ChannelChanged>(new
            {
                ChannelId = msg.ChannelId,
                GuildId = msg.GuildId,
                Name = string.Empty,
                Topic = (string?)null,
                ChannelType = 0,
                ParentId = (long?)null,
                IsPresent = false,
                CurrentState = "Inaccessible",
                UpdatedOn = clock.UtcNow,
            }, ct);

            await context.Publish<ChannelSyncCompleted>(new
            {
                ChannelId = msg.ChannelId,
                GuildId = msg.GuildId,
                CurrentState = "CaughtUp",
                UpdatedOn = clock.UtcNow,
                Name = string.Empty,
                ChannelType = 0,
                ParentId = (long?)null,
                LastSyncedSnowflake = cursor,
                MessageCount = 0,
                IsCaughtUpAtLastPoll = true,
            }, ct);

            return;
        }

        var caughtUp = messageCount < PageSize;

        // Channel identity fields (Name, ChannelType, ParentId) aren't on ChannelSyncDue.
        // The saga keeps them as default until ChannelChanged events from GuildSyncConsumer arrive.
        await context.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = msg.ChannelId,
            GuildId = msg.GuildId,
            CurrentState = "CaughtUp",
            UpdatedOn = clock.UtcNow,
            Name = string.Empty,
            ChannelType = 0,
            ParentId = (long?)null,
            LastSyncedSnowflake = highestSnowflake,
            MessageCount = messageCount,
            IsCaughtUpAtLastPoll = caughtUp,
        }, ct);

        logger.LogDebug(
            "Channel {ChannelId}: {Count} messages captured, cursor={Cursor}, caughtUp={CaughtUp}",
            msg.ChannelId, messageCount, highestSnowflake, caughtUp);
    }

    /// <summary>
    /// Parses author.id and author.bot from the raw Discord message payload.
    /// Returns (0L, false) on any parse failure so a bad payload never blocks capture.
    /// </summary>
    private static (long AuthorId, bool IsBot) ParseAuthor(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload, JsonOpts);
            if (!doc.RootElement.TryGetProperty("author", out var author))
                return (0L, false);

            var authorId = author.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? long.Parse(idEl.GetString()!, CultureInfo.InvariantCulture)
                : 0L;

            // Discord omits the "bot" field entirely for human users; treat absent as false.
            var isBot = author.TryGetProperty("bot", out var botEl) && botEl.ValueKind == JsonValueKind.True;

            return (authorId, isBot);
        }
        catch
        {
            return (0L, false);
        }
    }
}
