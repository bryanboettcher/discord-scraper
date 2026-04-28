using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Discord;
using DiscordScraper.Discord.Models;
using DiscordScraper.Write.Consumers;
using DiscordScraper.Write.Repositories;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Sagas;

[TestFixture]
public class GuildSagaStateMachineTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static ISystemClock MakeClock()
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(FixedNow);
        return clock;
    }

    // -------------------------------------------------------------------------
    // Test 1: GuildSyncRequested creates saga in Syncing state
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildSyncRequested_creates_saga_in_Syncing_state()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 123456789012345678L;
        var expectedCorrelationId = DeterministicGuid.FromSnowflake(guildId);

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId,
            CurrentState = "Initial",
            LastUpdatedAt = FixedNow,
        });

        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        var sagaId = await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);
        sagaId.ShouldNotBeNull("Saga was not created or did not enter Syncing state");

        var saga = sagaHarness.Sagas.Contains(expectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.GuildId.ShouldBe(guildId);
        saga.CorrelationId.ShouldBe(expectedCorrelationId);
        saga.LastUpdatedAt.ShouldBe(FixedNow);
    }

    // -------------------------------------------------------------------------
    // Test 2: GuildChanged transitions Syncing → Synced and updates metadata
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildChanged_transitions_to_Synced_and_updates_metadata()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 987654321098765432L;
        var expectedCorrelationId = DeterministicGuid.FromSnowflake(guildId);

        // Drive into Syncing first.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId,
            CurrentState = "Initial",
            LastUpdatedAt = FixedNow,
        });

        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();
        await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);

        // Now GuildChanged arrives from GuildSyncConsumer.
        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = "My Test Guild",
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });

        var sagaId = await sagaHarness.Exists(expectedCorrelationId, m => m.Synced);
        sagaId.ShouldNotBeNull("Saga did not transition to Synced");

        var saga = sagaHarness.Sagas.Contains(expectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.Name.ShouldBe("My Test Guild");
        saga.LastSyncedAt.ShouldBe(FixedNow);
        saga.LastUpdatedAt.ShouldBe(FixedNow);
    }

    // -------------------------------------------------------------------------
    // Test 3: Re-publishing GuildSyncRequested while Synced re-enters Syncing
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildSyncRequested_while_Synced_re_enters_Syncing()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 111222333444555666L;
        var expectedCorrelationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // First cycle: Initial → Syncing → Synced.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId,
            CurrentState = "Initial",
            LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = "Guild v1",
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Synced);

        // Second cycle: scheduler fires again while Synced → must re-enter Syncing.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId,
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });

        var sagaId = await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);
        sagaId.ShouldNotBeNull("Saga did not re-enter Syncing after second GuildSyncRequested");
    }

    // -------------------------------------------------------------------------
    // Test 4: GuildChanged with roles → saga.Roles populated
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildChanged_with_roles_populates_saga_roles()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 777888999000111222L;
        var expectedCorrelationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = "Roles Guild",
            Roles = new List<GuildRole>
            {
                new(1001L, "Admin"),
                new(1002L, "Member"),
            },
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });

        await sagaHarness.Exists(expectedCorrelationId, m => m.Synced);

        var saga = sagaHarness.Sagas.Contains(expectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.Roles.Count.ShouldBe(2);
        saga.Roles.ShouldContain(r => r.Id == 1001L && r.Name == "Admin");
        saga.Roles.ShouldContain(r => r.Id == 1002L && r.Name == "Member");
    }

    // -------------------------------------------------------------------------
    // Test 5: Re-publishing GuildChanged with a different role set replaces (not merges) roles
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildChanged_second_publish_replaces_roles_not_merges()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 333444555666777888L;
        var expectedCorrelationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // First sync cycle — initial roles.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = "My Guild",
            Roles = new List<GuildRole> { new(100L, "OldRole") },
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Synced);

        // Second sync cycle — Discord returns a different authoritative set.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Synced", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId,
            Name = "My Guild",
            Roles = new List<GuildRole> { new(200L, "NewRole"), new(201L, "AnotherRole") },
            CurrentState = "Synced",
            LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(expectedCorrelationId, m => m.Synced);

        var saga = sagaHarness.Sagas.Contains(expectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.Roles.Count.ShouldBe(2, "second GuildChanged replaces — not merges — the role list");
        saga.Roles.ShouldNotContain(r => r.Id == 100L, "OldRole from first cycle must not survive");
        saga.Roles.ShouldContain(r => r.Id == 200L && r.Name == "NewRole");
        saga.Roles.ShouldContain(r => r.Id == 201L && r.Name == "AnotherRole");
    }

    // -------------------------------------------------------------------------
    // Test 6 (original 4): GuildSyncConsumer publishes ChannelSyncRequested + GuildChanged
    // -------------------------------------------------------------------------

    [Test]
    public async Task GuildSyncConsumer_publishes_correct_channel_events_and_GuildChanged()
    {
        var discordClient = Substitute.For<IDiscordClient>();

        discordClient.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordGuildRaw(GuildId: 42L, Name: "FakeGuild", Payload: "{}"));

        // 3 text channels (types 0, 5, 0) + 1 voice (type 2, should be excluded).
        discordClient.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new DiscordChannelRaw(ChannelId: 1L, GuildId: 42L, Type: 0, ParentId: null, Name: "general", Payload: "{}"),
                new DiscordChannelRaw(ChannelId: 2L, GuildId: 42L, Type: 5, ParentId: null, Name: "announcements", Payload: "{}"),
                new DiscordChannelRaw(ChannelId: 3L, GuildId: 42L, Type: 2, ParentId: null, Name: "voice", Payload: "{}"),
            ]);

        // 1 active thread (type 11).
        discordClient.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([
                new DiscordChannelRaw(ChannelId: 4L, GuildId: 42L, Type: 11, ParentId: 1L, Name: "a-thread", Payload: "{}"),
            ]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, long>());

        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        await using var provider = new ServiceCollection()
            .AddSingleton(discordClient)
            .AddSingleton(cursorRepo)
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<GuildSyncConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = 42L,
            CurrentState = "Initial",
            LastUpdatedAt = DateTimeOffset.UtcNow,
        });

        // 3 text-like channels (1,2,4) + 1 GuildChanged = 4 publishes.
        (await harness.Published.Any<ChannelSyncRequested>()).ShouldBeTrue("No ChannelSyncRequested published");
        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue("No GuildChanged published");

        var channelEvents = harness.Published
            .Select<ChannelSyncRequested>()
            .ToList();
        channelEvents.Count.ShouldBe(3, "Expected exactly 3 text-like ChannelSyncRequested (voice excluded)");

        var publishedChannelIds = channelEvents
            .Select(e => e.Context.Message.ChannelId)
            .OrderBy(id => id)
            .ToList();
        publishedChannelIds.ShouldBe([1L, 2L, 4L]);

        var guildChanged = harness.Published
            .Select<GuildChanged>()
            .Single();
        guildChanged.Context.Message.Name.ShouldBe("FakeGuild");
    }

    // -------------------------------------------------------------------------
    // Heartbeat tests — verify CorrelateBy fan-out via in-memory repo
    // MT's InMemorySagaRepository implements IQuerySagaRepository<T>, so the
    // ExpressionCorrelationSagaQueryFactory-backed CorrelateBy is fully exercised
    // in the test harness without needing a real Mongo instance.
    // -------------------------------------------------------------------------

    [Test]
    public async Task Heartbeat_stale_saga_Synced_transitions_to_Syncing()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 500000000000000001L;
        var correlationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // Bootstrap: SyncRequested → Syncing, then GuildChanged → Synced.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId, Name = "Heartbeat Guild", CurrentState = "Synced", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Synced);

        // Heartbeat: StaleAfter is after LastSyncedAt (FixedNow) — saga should match and re-enter Syncing.
        var heartbeatTime = FixedNow.AddMinutes(10);
        await harness.Bus.Publish<SyncHeartbeat>(new
        {
            Timestamp = heartbeatTime,
            StaleAfter = heartbeatTime.AddMinutes(-5), // StaleAfter > FixedNow (LastSyncedAt)
        });

        var sagaId = await sagaHarness.Exists(correlationId, m => m.Syncing);
        sagaId.ShouldNotBeNull("Stale Synced saga did not re-enter Syncing on heartbeat");
    }

    [Test]
    public async Task Heartbeat_fresh_saga_stays_Synced()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 500000000000000002L;
        var correlationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // Bootstrap into Synced — LastSyncedAt will be FixedNow.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId, Name = "Fresh Guild", CurrentState = "Synced", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Synced);

        // Heartbeat: StaleAfter is before LastSyncedAt — saga is fresh and must NOT match.
        var heartbeatTime = FixedNow.AddMinutes(10);
        await harness.Bus.Publish<SyncHeartbeat>(new
        {
            Timestamp = heartbeatTime,
            StaleAfter = FixedNow.AddMinutes(-5), // StaleAfter < FixedNow (LastSyncedAt)
        });

        // Give the harness a moment to process — no state change expected.
        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(correlationId);
        saga.ShouldNotBeNull();
        saga.CurrentState.ShouldBe("Synced", "Fresh saga must not be picked up by heartbeat with future StaleAfter cutoff");
    }

    [Test]
    public async Task Heartbeat_while_Syncing_is_ignored()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 500000000000000003L;
        var correlationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // Bootstrap into Syncing — don't publish GuildChanged so it stays Syncing.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Syncing);

        // Heartbeat arrives while in Syncing — CorrelateBy excludes CurrentState == "Syncing"
        // so this heartbeat should not match the saga at all (Discard on missing).
        var heartbeatTime = FixedNow.AddMinutes(10);
        await harness.Bus.Publish<SyncHeartbeat>(new
        {
            Timestamp = heartbeatTime,
            StaleAfter = heartbeatTime.AddMinutes(-5),
        });

        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(correlationId);
        saga.ShouldNotBeNull();
        saga.CurrentState.ShouldBe("Syncing", "Saga in Syncing must be excluded by CorrelateBy filter");
    }

    [Test]
    public async Task Heartbeat_IsPresent_false_is_not_matched()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 500000000000000004L;
        var correlationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        // Bootstrap into Synced.
        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Syncing);

        await harness.Bus.Publish<GuildChanged>(new
        {
            GuildId = guildId, Name = "Absent Guild", CurrentState = "Synced", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Synced);

        // Simulate IsPresent being flipped to false (e.g. via admin action).
        var saga = sagaHarness.Sagas.Contains(correlationId);
        saga.ShouldNotBeNull();
        saga.IsPresent = false;

        // Heartbeat would normally match (stale cutoff after LastSyncedAt) but IsPresent blocks it.
        var heartbeatTime = FixedNow.AddMinutes(10);
        await harness.Bus.Publish<SyncHeartbeat>(new
        {
            Timestamp = heartbeatTime,
            StaleAfter = heartbeatTime.AddMinutes(-5),
        });

        await Task.Delay(200);

        saga = sagaHarness.Sagas.Contains(correlationId);
        saga.ShouldNotBeNull();
        saga.CurrentState.ShouldBe("Synced", "Saga with IsPresent=false must not be picked up by heartbeat");
    }

    [Test]
    public async Task New_saga_IsPresent_defaults_to_true()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<GuildSagaStateMachine, GuildSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long guildId = 500000000000000005L;
        var correlationId = DeterministicGuid.FromSnowflake(guildId);
        var sagaHarness = harness.GetSagaStateMachineHarness<GuildSagaStateMachine, GuildSagaState>();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            GuildId = guildId, CurrentState = "Initial", LastUpdatedAt = FixedNow,
        });
        await sagaHarness.Exists(correlationId, m => m.Syncing);

        var saga = sagaHarness.Sagas.Contains(correlationId);
        saga.ShouldNotBeNull();
        saga.IsPresent.ShouldBeTrue("Newly registered saga must default to IsPresent = true");
    }
}
