using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Sync;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Tracks the sync lifecycle for a guild. GuildSyncRequested transitions to Syncing; GuildChanged
/// (published by GuildSyncConsumer after fetching guild metadata) transitions to Synced. No terminal
/// state. Pub/sub rather than request/response because GuildSyncConsumer is a fan-out — it publishes
/// ChannelSyncRequested for each channel plus GuildChanged independently.
/// </summary>
public sealed class GuildSagaStateMachine : MassTransitStateMachine<GuildSagaState>
{
    public GuildSagaStateMachine(ISystemClock clock)
    {
        InstanceState(x => x.CurrentState);

        // InsertOnInitial=true: first GuildSyncRequested upserts in one round-trip rather than
        // insert-then-update, halving Mongo I/O during the initial backfill burst.
        Event(() => SyncRequested, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.GuildId));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => new GuildSagaState
            {
                CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.GuildId),
                GuildId = ctx.Message.GuildId,
                LastUpdatedAt = clock.UtcNow,
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
            // since heartbeats never bootstrap new sagas (only SyncRequested does that).
            e.OnMissingInstance(m => m.Discard());
        });

        Initially(
            When(SyncRequested)
                .Then(ctx =>
                {
                    ctx.Saga.GuildId = ctx.Message.GuildId;
                    ctx.Saga.CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.GuildId);
                    ctx.Saga.IsPresent = true;
                    BeginSyncing(ctx.Saga, clock);
                })
                .TransitionTo(Syncing));

        // Steady-state loop: explicit request or heartbeat pick-up while Synced re-enters Syncing.
        During(Synced,
            When(SyncRequested)
                .Then(ctx => BeginSyncing(ctx.Saga, clock))
                .TransitionTo(Syncing),

            When(Heartbeat)
                .Then(ctx => BeginSyncing(ctx.Saga, clock))
                .TransitionTo(Syncing),

            // GuildChanged received while already Synced (e.g., gateway push) — update in place.
            When(Changed).Then(ctx => ApplyChanged(ctx.Saga, ctx.Message, clock)));

        // GuildChanged received during Syncing settles the saga.
        During(Syncing,
            When(Changed)
                .Then(ctx => ApplyChanged(ctx.Saga, ctx.Message, clock))
                .TransitionTo(Synced));
    }

    private static void BeginSyncing(GuildSagaState saga, ISystemClock clock)
    {
        saga.LastUpdatedAt = clock.UtcNow;
    }

    private static void ApplyChanged(GuildSagaState saga, GuildChanged msg, ISystemClock clock)
    {
        saga.Name = msg.Name;
        saga.Roles = msg.Roles;
        saga.LastSyncedAt = clock.UtcNow;
        saga.LastUpdatedAt = clock.UtcNow;
    }

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Syncing { get; private set; } = null!;
    public State Synced { get; private set; } = null!;

    public Event<GuildSyncRequested> SyncRequested { get; private set; } = null!;
    public Event<GuildChanged> Changed { get; private set; } = null!;
    public Event<SyncHeartbeat> Heartbeat { get; private set; } = null!;
}
