using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Sync;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Tracks the sync lifecycle for a guild. GuildSyncDue transitions to Syncing; GuildChanged
/// (published by GuildSyncConsumer after fetching guild metadata) transitions to Synced. No terminal
/// state. Pub/sub rather than request/response because GuildSyncConsumer is a fan-out — it publishes
/// ChannelSyncDue for each channel plus GuildChanged independently.
/// </summary>
public sealed class GuildSagaStateMachine : MassTransitStateMachine<GuildSagaState>
{
    public GuildSagaStateMachine(ISystemClock clock)
    {
        InstanceState(x => x.CurrentState);

        // InsertOnInitial=true: first GuildSyncDue upserts in one round-trip rather than
        // insert-then-update, halving Mongo I/O during the initial backfill burst.
        Event(() => SyncDue, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.GuildId));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => new GuildSagaState
            {
                CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.GuildId),
                GuildId = ctx.Message.GuildId,
            });
        });

        Event(() => Changed, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.GuildId)));

        // CorrelateBy dispatches one heartbeat to every matching saga, serially.
        // MT's ExpressionCorrelationSagaQueryFactory evaluates this as a store-side query:
        // the message-side reference (ctx.Message.StaleAfter) is folded to a constant at
        // dispatch time via EventCorrelationExpressionConverter, leaving a pure saga-predicate
        // expression that the Mongo repo translates to a Find filter.
        // API form: Expression<Func<TInstance, ConsumeContext<TData>, bool>> — confirmed in
        // MassTransit.Abstractions IEventCorrelationConfigurator and Hangfire integration tests.
        Event(() => Heartbeat, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.LastSyncedAt < ctx.Message.StaleAfter
                && saga.IsPresent
                && saga.CurrentState != nameof(Syncing));

            // Heartbeats that don't match any saga are silently discarded — correct behaviour
            // since heartbeats never bootstrap new sagas (only SyncDue does that).
            e.OnMissingInstance(m => m.Discard());
        });

        Initially(
            When(SyncDue)
                .Then(ctx =>
                {
                    ctx.Saga.GuildId = ctx.Message.GuildId;
                    ctx.Saga.CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.GuildId);
                    ctx.Saga.IsPresent = true;
                })
                .TransitionTo(Syncing));

        // Steady-state loop: explicit request or heartbeat pick-up while Synced re-enters Syncing.
        During(Synced,
            When(SyncDue)
                .TransitionTo(Syncing),

            When(Heartbeat)
                .TransitionTo(Syncing),

            // GuildChanged received while already Synced (e.g., gateway push) — update in place.
            When(Changed).Then(ctx => ApplyChanged(ctx.Saga, ctx.Message, clock)));

        // GuildChanged received during Syncing settles the saga.
        During(Syncing,
            When(Changed)
                .Then(ctx => ApplyChanged(ctx.Saga, ctx.Message, clock))
                .Then(ctx => SettleSaga(ctx, clock))
                .TransitionTo(Synced));

        // Catch-all: every event bumps UpdatedOn and initialises CreatedOn once.
        During(
            Initial, Syncing, Synced,
            When(SyncDue).Then(ctx => UpdateSaga(ctx, clock)),
            When(Changed).Then(ctx => UpdateSaga(ctx, clock)),
            When(Heartbeat).Then(ctx => UpdateSaga(ctx, clock)));
    }

    private static void ApplyChanged(GuildSagaState saga, GuildChanged msg, ISystemClock clock)
    {
        saga.Name = msg.Name;
        saga.Roles = msg.Roles;
        // Only ratchet to absent — presence is restored via admin action, not via event.
        // IsPresent=false is the sentinel; IsPresent=true (the default) doesn't restore a revoked saga.
        if (!msg.IsPresent)
            saga.IsPresent = false;
        saga.LastSyncedAt = clock.UtcNow;
    }

    private static void UpdateSaga(BehaviorContext<GuildSagaState> ctx, ISystemClock clock)
    {
        var now = clock.UtcNow;
        if (ctx.Saga.CreatedOn == default) ctx.Saga.CreatedOn = now;
        ctx.Saga.UpdatedOn = now;
    }

    private static void SettleSaga(BehaviorContext<GuildSagaState> ctx, ISystemClock clock)
        => ctx.Saga.SettledOn = clock.UtcNow;

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Syncing { get; private set; } = null!;
    public State Synced { get; private set; } = null!;

    public Event<GuildSyncDue> SyncDue { get; private set; } = null!;
    public Event<GuildChanged> Changed { get; private set; } = null!;
    public Event<SyncHeartbeat> Heartbeat { get; private set; } = null!;
}
