// CS8618/CS9264: MT initializes Event/State/Request properties via reflection on backing fields.
// CS8602/CS8601: dereferencing those properties in the constructor (e.g. State[] allStates,
// catch-all During loops) is safe at runtime but unprovable by flow analysis.
#pragma warning disable CS8618, CS9264, CS8602, CS8601

using DiscordScraper.Contracts;
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

    public ChannelSagaStateMachine()
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
                .Then(UpdateSaga)
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
                .Schedule(PinPollSchedule, ctx => ctx.ToPinPollDue(PinPollDelay)));

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
                    ctx.Saga.LastSyncedAt = ctx.SentTime ?? ctx.Saga.UpdatedOn;
                })
                .Then(SettleSaga)
                .Schedule(PinPollSchedule, ctx => ctx.ToPinPollDue(PinPollDelay))
                .TransitionTo(CaughtUp));

        // Catch-all: every event bumps UpdatedOn and initialises CreatedOn once.
        // Initial is excluded for the same reason as MessageSagaStateMachine: including Initial
        // would make every event here initial-reachable, selecting NewOrExistingSagaPolicy on miss.
        // Scheduled events (e.g. PinPollDue) that arrive after a saga is gone would then try to
        // insert a duplicate document and hit E11000. CreatedOn/UpdatedOn for the first SyncDue
        // are stamped by the explicit .Then(UpdateSaga) in the Initially block above.
        During(
            Syncing, CaughtUp,
            When(SyncDue).Then(UpdateSaga),
            When(SyncCompleted).Then(UpdateSaga),
            When(Changed).Then(UpdateSaga),
            When(PinSetChanged).Then(UpdateSaga),
            When(PinPollSchedule.Received).Then(UpdateSaga));
    }

    private static void ApplyChannelChanged(ChannelSagaState saga, ChannelChanged msg)
    {
        // Only ratchet to absent — presence is restored via admin action, not via event.
        if (!msg.IsPresent)
            saga.IsPresent = false;
    }

    private static void UpdateSaga(BehaviorContext<ChannelSagaState> ctx)
    {
        var now = ctx.SentTime ?? ctx.Saga.UpdatedOn;
        if (ctx.Saga.CreatedOn == default) ctx.Saga.CreatedOn = now;
        ctx.Saga.UpdatedOn = now;
    }

    private static void SettleSaga(BehaviorContext<ChannelSagaState> ctx)
        => ctx.Saga.SettledOn = ctx.SentTime ?? ctx.Saga.UpdatedOn;

    public State Syncing { get; }
    public State CaughtUp { get; }

    public Event<ChannelSyncDue> SyncDue { get; }
    public Event<ChannelSyncCompleted> SyncCompleted { get; }
    public Event<ChannelChanged> Changed { get; }
    public Event<PinSetChanged> PinSetChanged { get; }

    public Schedule<ChannelSagaState, PinPollDue> PinPollSchedule { get; }
}
