using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Discord;
using DiscordScraper.Write.Pins;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Consumers;

/// <summary>
/// Stateless consumer that executes a pin poll for a single channel.
/// Receives the scheduled PinPollDue trigger, fetches /channels/{id}/pins,
/// and publishes:
///   - PinSetChanged (always) — ChannelSaga owns the no-op-vs-changed decision.
///   - MessageEditObserved (one per pinned message with a non-null edited_timestamp).
///
/// AuthorId is set to 0L in MessageEditObserved because the pins endpoint does not expose
/// the author snowflake separately from the full payload JSON.  The MessageSaga already
/// holds the correct AuthorId on its state and does not use the event's AuthorId for
/// re-projection — only PayloadJson is consumed there.
///
/// Edit deduplication is not done here; publishing unconditionally for all edited pins is
/// acceptable because re-projection of an unchanged IR is idempotent.  A future optimization
/// can track per-message edit timestamps in ChannelSagaState to suppress duplicates.
/// </summary>
public sealed class PinPollConsumer(
    IDiscordClient discord,
    TimeProvider clock,
    ILogger<PinPollConsumer> logger) : IConsumer<PinPollDue>
{
    public async Task Consume(ConsumeContext<PinPollDue> context)
    {
        var due = context.Message;

        logger.LogDebug(
            "Pin poll executing for ChannelId={ChannelId} GuildId={GuildId}",
            due.ChannelId, due.GuildId);

        var pinsResponse = await discord.GetChannelPinsAsync(
            due.ChannelId.ToString(),
            due.GuildId,
            context.CancellationToken);

        var snapshots = pinsResponse.Messages
            .Select(m => new PinSnapshot(m.MessageId, m.EditedAt))
            .ToList();

        var canonical = PinSetCanonicalizer.Canonicalize(snapshots);
        var observedAt = clock.GetUtcNow();

        await context.Publish<PinSetChanged>(new
        {
            due.ChannelId,
            due.GuildId,
            CurrentState = "CaughtUp",
            CanonicalHash = canonical,
            PinCount = snapshots.Count,
            ObservedAt = observedAt,
            UpdatedOn = observedAt,
        });

        // Publish MessageEditObserved for every pinned message that has been edited.
        // The full payload from the pins response is used as UpdatedPayloadJson so the
        // MessageSaga's re-projection sees current content without an extra API call.
        foreach (var msg in pinsResponse.Messages.Where(m => m.EditedAt.HasValue))
        {
            await context.Publish<MessageEditObserved>(new
            {
                MessageSnowflake = msg.MessageId,
                MessageId = DeterministicGuid.FromSnowflake(msg.MessageId),
                due.ChannelId,
                due.GuildId,
                AuthorId = 0L,
                CurrentState = "EditObserved",
                EditedAt = msg.EditedAt!.Value,
                UpdatedPayloadJson = msg.Payload,
                UpdatedOn = observedAt,
            });
        }

        logger.LogDebug(
            "Pin poll complete ChannelId={ChannelId}: {PinCount} pins, {EditCount} edit events",
            due.ChannelId, snapshots.Count, pinsResponse.Messages.Count(m => m.EditedAt.HasValue));
    }
}
