using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Tracks the sync lifecycle for a channel. No terminal state — channels are monitored for as
/// long as they exist. The cursor (LastSyncedSnowflake) is stamped onto the next
/// ChannelSyncDue so the consumer can resume without querying Mongo directly.
/// </summary>
/// <remarks>
/// States: <c>Syncing</c> while a pass is in flight, <c>CaughtUp</c> once it completes.
/// Re-receiving ChannelSyncDue in CaughtUp re-enters Syncing (the steady-state loop
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

        // InsertOnInitial=true: first ChannelSyncDue upserts in one round-trip rather than
        // insert-then-update, halving Mongo I/O during the initial backfill burst.
        Event(() => SyncDue, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => new ChannelSagaState
            {
                CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.ChannelId),
                ChannelId = ctx.Message.ChannelId,
                GuildId = ctx.Message.GuildId,
                IsPresent = true,
            });
        });

        Event(() => SyncCompleted, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId)));

        Event(() => Changed, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.ChannelId));
            // ChannelChanged can arrive for a channel that has no saga yet (e.g. GuildSyncConsumer
            // races ahead of ChannelSyncDue). Discard rather than creating a dangling saga.
            e.OnMissingInstance(m => m.Discard());
        });

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
            When(SyncDue)
                .Then(ctx =>
                {
                    ctx.Saga.ChannelId = ctx.Message.ChannelId;
                    ctx.Saga.GuildId = ctx.Message.GuildId;
                    ctx.Saga.CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.ChannelId);
                })
                .TransitionTo(Syncing));

        During(CaughtUp,
            When(SyncDue)
                .Then(ctx =>
                {
                    // The new pass will recompute IsCaughtUpAtLastPoll from its page size.
                    ctx.Saga.IsCaughtUpAtLastPoll = false;
                })
                .TransitionTo(Syncing),

            When(Changed)
                .Then(ctx => ApplyChannelChanged(ctx.Saga, ctx.Message)),

            // PinPollDue is consumed by PinPollConsumer for the actual fetch; the saga only
            // needs to acknowledge the scheduled delivery so MT doesn't fault. PinSetChanged
            // arrives separately and drives the cursor + reschedule.
            When(PinPollSchedule.Received).Then(_ => { }),

            When(PinSetChanged)
                .Then(ctx =>
                {
                    ctx.Saga.PinSetCanonical = ctx.Message.CanonicalHash;
                })
                .Schedule(PinPollSchedule, ctx => ctx.Init<PinPollDue>(new
                {
                    ctx.Saga.ChannelId,
                    ctx.Saga.GuildId,
                    CurrentState = ctx.Saga.CurrentState,
                    DueAt = clock.UtcNow.Add(PinPollDelay),
                    UpdatedOn = ctx.Saga.UpdatedOn,
                })));

        During(Syncing,
            // A second ChannelSyncDue arriving while already syncing (e.g. scheduler fires
            // before the long backfill pass finishes) is silently dropped. Without this handler
            // MassTransit would fault the message to the DLQ on every slow-channel backfill.
            When(SyncDue).Then(_ => { }),

            // ChannelChanged with IsPresent=false arrives before SyncCompleted when the channel
            // is inaccessible. Mirror the flag immediately so the saga state is consistent.
            When(Changed)
                .Then(ctx => ApplyChannelChanged(ctx.Saga, ctx.Message)),

            // PinPollDue can fire while we're stuck in Syncing (retries, slow backfill). There is
            // no pin fetch to do while syncing; drop the message. When SyncCompleted eventually
            // arrives it will schedule a fresh PinPollDue via the normal CaughtUp path.
            When(PinPollSchedule.Received).Then(_ => { }),

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
                })
                .Then(ctx => SettleSaga(ctx, clock))
                .Schedule(PinPollSchedule, ctx => ctx.Init<PinPollDue>(new
                {
                    ctx.Saga.ChannelId,
                    ctx.Saga.GuildId,
                    CurrentState = ctx.Saga.CurrentState,
                    DueAt = clock.UtcNow.Add(PinPollDelay),
                    UpdatedOn = ctx.Saga.UpdatedOn,
                }))
                .TransitionTo(CaughtUp));

        // Catch-all: every event bumps UpdatedOn and initialises CreatedOn once.
        During(
            Initial, Syncing, CaughtUp,
            When(SyncDue).Then(ctx => UpdateSaga(ctx, clock)),
            When(SyncCompleted).Then(ctx => UpdateSaga(ctx, clock)),
            When(Changed).Then(ctx => UpdateSaga(ctx, clock)),
            When(PinSetChanged).Then(ctx => UpdateSaga(ctx, clock)),
            When(PinPollSchedule.Received).Then(ctx => UpdateSaga(ctx, clock)));
    }

    private static void ApplyChannelChanged(ChannelSagaState saga, ChannelChanged msg)
    {
        // Only ratchet to absent — presence is restored via admin action, not via event.
        if (!msg.IsPresent)
            saga.IsPresent = false;
    }

    private static void UpdateSaga(BehaviorContext<ChannelSagaState> ctx, ISystemClock clock)
    {
        var now = clock.UtcNow;
        if (ctx.Saga.CreatedOn == default) ctx.Saga.CreatedOn = now;
        ctx.Saga.UpdatedOn = now;
    }

    private static void SettleSaga(BehaviorContext<ChannelSagaState> ctx, ISystemClock clock)
        => ctx.Saga.SettledOn = clock.UtcNow;

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Syncing { get; private set; } = null!;
    public State CaughtUp { get; private set; } = null!;

    public Event<ChannelSyncDue> SyncDue { get; private set; } = null!;
    public Event<ChannelSyncCompleted> SyncCompleted { get; private set; } = null!;
    public Event<ChannelChanged> Changed { get; private set; } = null!;
    public Event<PinSetChanged> PinSetChanged { get; private set; } = null!;

    public Schedule<ChannelSagaState, PinPollDue> PinPollSchedule { get; private set; } = null!;
}
