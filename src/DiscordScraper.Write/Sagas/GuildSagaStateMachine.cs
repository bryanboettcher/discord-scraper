using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Guild;
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

        Initially(
            When(SyncRequested)
                .Then(ctx =>
                {
                    ctx.Saga.GuildId = ctx.Message.GuildId;
                    ctx.Saga.CorrelationId = DeterministicGuid.FromSnowflake(ctx.Message.GuildId);
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .TransitionTo(Syncing));

        // Steady-state loop: scheduler periodically re-fires SyncRequested while Synced.
        During(Synced,
            When(SyncRequested)
                .Then(ctx => ctx.Saga.LastUpdatedAt = clock.UtcNow)
                .TransitionTo(Syncing),

            // GuildChanged received while already Synced (e.g., gateway push) — update in place.
            When(Changed).Then(ctx => ApplyChanged(ctx.Saga, ctx.Message.Name, clock)));

        // GuildChanged received during Syncing settles the saga.
        During(Syncing,
            When(Changed)
                .Then(ctx => ApplyChanged(ctx.Saga, ctx.Message.Name, clock))
                .TransitionTo(Synced));
    }

    private static void ApplyChanged(GuildSagaState saga, string name, ISystemClock clock)
    {
        saga.Name = name;
        saga.LastSyncedAt = clock.UtcNow;
        saga.LastUpdatedAt = clock.UtcNow;
    }

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Syncing { get; private set; } = null!;
    public State Synced { get; private set; } = null!;

    public Event<GuildSyncRequested> SyncRequested { get; private set; } = null!;
    public Event<GuildChanged> Changed { get; private set; } = null!;
}
