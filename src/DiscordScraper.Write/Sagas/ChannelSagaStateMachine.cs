using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Tracks the sync lifecycle for a channel. No terminal state — channels are monitored for as
/// long as they exist. The cursor (LastSyncedSnowflake) is stamped onto the next
/// ChannelSyncRequested so the consumer can resume without querying Mongo directly.
/// </summary>
/// <remarks>
/// States: <c>Syncing</c> while a pass is in flight, <c>CaughtUp</c> once it completes.
/// Re-receiving ChannelSyncRequested in CaughtUp re-enters Syncing (the steady-state loop
/// driven by SyncSchedulerService). Pins are polled every <see cref="PinPollDelay"/> via
/// <see cref="PinPollSchedule"/>; PinSetChanged updates PinSetCanonical and reschedules.
/// </remarks>
public sealed class ChannelSagaStateMachine : MassTransitStateMachine<ChannelSagaState>
{
    // Pin poll cadence. Lift to options if a deployment needs a different value.
    private static readonly TimeSpan PinPollDelay = TimeSpan.FromMinutes(5);

    public ChannelSagaStateMachine(ISystemClock clock)
    {
        InstanceState(x => x.CurrentState);

        // InsertOnInitial=true: first ChannelSyncRequested upserts in one round-trip rather than
        // insert-then-update, halving Mongo I/O during the initial backfill burst.
        Event(() => SyncRequested, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => new ChannelSagaState
            {
                CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.ChannelId),
                ChannelId = ctx.Message.ChannelId,
                GuildId = ctx.Message.GuildId,
                LastUpdatedAt = clock.UtcNow,
                IsPresent = true,
            });
        });

        Event(() => SyncCompleted, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId)));

        Event(() => PinSetChanged, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId)));

        // Deferred PinPollDue delivery; the schedule token in PinPollScheduleId lets MT cancel
        // the in-flight scheduled message before rescheduling, preventing duplicate polls.
        Schedule(() => PinPollSchedule, instance => instance.PinPollScheduleId, s =>
        {
            s.Delay = PinPollDelay;
            s.Received = e =>
                e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId));
        });

        Initially(
            When(SyncRequested)
                .Then(ctx =>
                {
                    ctx.Saga.ChannelId = ctx.Message.ChannelId;
                    ctx.Saga.GuildId = ctx.Message.GuildId;
                    ctx.Saga.CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.ChannelId);
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .TransitionTo(Syncing));

        During(CaughtUp,
            When(SyncRequested)
                .Then(ctx =>
                {
                    // The new pass will recompute IsCaughtUpAtLastPoll from its page size.
                    ctx.Saga.IsCaughtUpAtLastPoll = false;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .TransitionTo(Syncing),

            When(PinSetChanged)
                .Then(ctx =>
                {
                    ctx.Saga.PinSetCanonical = ctx.Message.CanonicalHash;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .Schedule(PinPollSchedule, ctx => ctx.Init<PinPollDue>(new
                {
                    ctx.Saga.ChannelId,
                    ctx.Saga.GuildId,
                    CurrentState = ctx.Saga.CurrentState,
                    DueAt = clock.UtcNow.Add(PinPollDelay),
                    LastUpdatedAt = clock.UtcNow,
                })));

        During(Syncing,
            // A second ChannelSyncRequested arriving while already syncing (e.g. scheduler fires
            // before the long backfill pass finishes) is silently dropped. Without this handler
            // MassTransit would fault the message to the DLQ on every slow-channel backfill.
            When(SyncRequested).Then(_ => { }),

            When(SyncCompleted)
                .Then(ctx =>
                {
                    ctx.Saga.Name = ctx.Message.Name;
                    ctx.Saga.ChannelType = ctx.Message.ChannelType;
                    ctx.Saga.ParentId = ctx.Message.ParentId;

                    // Only advance the cursor if the pass actually yielded messages; a zero-message
                    // pass on a brand-new channel should not overwrite a valid cursor.
                    if (ctx.Message.LastSyncedSnowflake > ctx.Saga.LastSyncedSnowflake)
                        ctx.Saga.LastSyncedSnowflake = ctx.Message.LastSyncedSnowflake;

                    ctx.Saga.IsCaughtUpAtLastPoll = ctx.Message.IsCaughtUpAtLastPoll;
                    ctx.Saga.LastSyncMessageCount = ctx.Message.MessageCount;
                    ctx.Saga.LastSyncedAt = clock.UtcNow;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .Schedule(PinPollSchedule, ctx => ctx.Init<PinPollDue>(new
                {
                    ctx.Saga.ChannelId,
                    ctx.Saga.GuildId,
                    CurrentState = ctx.Saga.CurrentState,
                    DueAt = clock.UtcNow.Add(PinPollDelay),
                    LastUpdatedAt = clock.UtcNow,
                }))
                .TransitionTo(CaughtUp));
    }

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Syncing { get; private set; } = null!;
    public State CaughtUp { get; private set; } = null!;

    public Event<ChannelSyncRequested> SyncRequested { get; private set; } = null!;
    public Event<ChannelSyncCompleted> SyncCompleted { get; private set; } = null!;
    public Event<PinSetChanged> PinSetChanged { get; private set; } = null!;

    public Schedule<ChannelSagaState, PinPollDue> PinPollSchedule { get; private set; } = null!;
}
