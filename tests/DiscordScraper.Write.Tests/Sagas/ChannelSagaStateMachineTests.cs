using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Channel;
using Microsoft.Extensions.Time.Testing;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Sagas;

[TestFixture]
public sealed class ChannelSagaStateMachineTests
{
    private const long TestChannelId = 1_000_000_000_000_000L;
    private const long TestGuildId   = 2_000_000_000_000_000L;

    private static Guid ExpectedCorrelationId => DeterministicGuid.FromSnowflake(TestChannelId);

    // ---------------------------------------------------------------------------
    // Init: ChannelSyncDue creates saga in Syncing
    // ---------------------------------------------------------------------------

    [Test]
    public async Task InitFromSyncDue_CreatesSagaInSyncingState()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId,
            GuildId   = TestGuildId,
            CurrentState = "Initial",
            UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();
        var exists = await sagaHarness.Exists(
            ExpectedCorrelationId,
            m => m.Syncing,
            TimeSpan.FromSeconds(5));

        exists.ShouldNotBeNull("Saga should exist in Syncing state after ChannelSyncDue");

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.ChannelId.ShouldBe(TestChannelId);
        saga.GuildId.ShouldBe(TestGuildId);
        saga.CorrelationId.ShouldBe(ExpectedCorrelationId);
    }

    // ---------------------------------------------------------------------------
    // Cursor advance: ChannelSyncCompleted moves to CaughtUp with updated cursor
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SyncCompleted_AdvancesCursorAndTransitionsToCaughtUp()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId,
            GuildId   = TestGuildId,
            CurrentState = "Initial",
            UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        const long newCursor = 999_000_000_000_000L;
        const int  msgCount  = 42;

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId              = TestChannelId,
            GuildId                = TestGuildId,
            CurrentState           = "CaughtUp",
            UpdatedOn          = DateTimeOffset.UtcNow,
            Name                   = "general",
            ChannelType            = 0,
            ParentId               = (long?)null,
            LastSyncedSnowflake    = newCursor,
            MessageCount           = msgCount,
            IsCaughtUpAtLastPoll   = true,
        });

        var exists = await sagaHarness.Exists(
            ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));
        exists.ShouldNotBeNull("Saga should be in CaughtUp after ChannelSyncCompleted");

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.LastSyncedSnowflake.ShouldBe(newCursor);
        saga.LastSyncMessageCount.ShouldBe(msgCount);
        saga.IsCaughtUpAtLastPoll.ShouldBeTrue();
        saga.Name.ShouldBe("general");
    }

    // ---------------------------------------------------------------------------
    // Re-entry: subsequent ChannelSyncDue while CaughtUp re-enters Syncing
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SubsequentSyncDue_WhileCaughtUp_ReentersSyncing()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        // First sync cycle
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            Name = "general", ChannelType = 0, ParentId = (long?)null,
            LastSyncedSnowflake = 100L, MessageCount = 5, IsCaughtUpAtLastPoll = true,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        // Second ChannelSyncDue should re-enter Syncing
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 100L,
        });

        var exists = await sagaHarness.Exists(
            ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));
        exists.ShouldNotBeNull("Saga should re-enter Syncing on second ChannelSyncDue");

        // IsCaughtUpAtLastPoll must be cleared when re-entering Syncing
        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.IsCaughtUpAtLastPoll.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------
    // Cursor guard: SyncCompleted with lower snowflake does not regress cursor
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SyncCompleted_WithLowerSnowflake_DoesNotRegressCursor()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            Name = "general", ChannelType = 0, ParentId = (long?)null,
            LastSyncedSnowflake = 500L, MessageCount = 10, IsCaughtUpAtLastPoll = false,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        // Second cycle — re-enter Syncing then complete with a LOWER snowflake (e.g. empty pass)
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 500L,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            Name = "general", ChannelType = 0, ParentId = (long?)null,
            LastSyncedSnowflake = 0L,   // empty pass; no new messages
            MessageCount = 0, IsCaughtUpAtLastPoll = true,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        // Cursor must not regress from 500 to 0
        saga.LastSyncedSnowflake.ShouldBe(500L);
    }

    // ---------------------------------------------------------------------------
    // Duplicate SyncDue while Syncing is silently ignored (WARNING 3)
    // ---------------------------------------------------------------------------

    [Test]
    public async Task DuplicateSyncDueWhileSyncing_IsIgnoredNotFaulted()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        // First SyncDue → Syncing
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        // Second SyncDue while still in Syncing — should not fault or throw
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 100L,
        });

        // Saga must still be in Syncing, not faulted
        var exists = await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));
        exists.ShouldNotBeNull("Saga should remain in Syncing after duplicate ChannelSyncDue");

        // No faulted messages
        harness.Published.Select<Fault>().ShouldBeEmpty("No fault should be published for a duplicate Syncing request");
    }

    // ---------------------------------------------------------------------------
    // PinPollDue while Syncing is silently dropped — no fault, saga stays Syncing
    // ---------------------------------------------------------------------------

    [Test]
    public async Task PinPollDueWhileSyncing_IsSilentlyDropped_SagaStaysSyncing()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        // Drive to Syncing and stay there (no SyncCompleted)
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        // Publish PinPollDue directly while the saga is stuck in Syncing
        await harness.Bus.Publish<PinPollDue>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            CurrentState = "Syncing",
            DueAt = DateTimeOffset.UtcNow,
            UpdatedOn = DateTimeOffset.UtcNow,
        });

        await Task.Delay(300);

        // Saga must still be in Syncing, not faulted
        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.CurrentState.ShouldBe("Syncing", "PinPollDue while Syncing must be dropped, not faulted");
        harness.Published.Select<Fault>().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // ChannelChanged(IsPresent=false) from any state sets saga.IsPresent=false
    // ---------------------------------------------------------------------------

    [Test]
    public async Task ChannelChanged_IsPresentFalse_WhileSyncing_SetsSagaIsPresentFalse()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelChanged>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            Name = string.Empty,
            Topic = (string?)null,
            ChannelType = 0,
            ParentId = (long?)null,
            IsPresent = false,
            CurrentState = "Inaccessible",
            UpdatedOn = DateTimeOffset.UtcNow,
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.IsPresent.ShouldBeFalse("ChannelChanged(IsPresent=false) while Syncing must flip saga.IsPresent");
        // Still in Syncing — ChannelChanged alone does not transition state
        saga.CurrentState.ShouldBe("Syncing");
    }

    [Test]
    public async Task ChannelChanged_IsPresentFalse_WhileCaughtUp_SetsSagaIsPresentFalse()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        // Drive to CaughtUp
        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "Initial", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = (long?)null,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", UpdatedOn = DateTimeOffset.UtcNow,
            Name = "general", ChannelType = 0, ParentId = (long?)null,
            LastSyncedSnowflake = 100L, MessageCount = 3, IsCaughtUpAtLastPoll = true,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelChanged>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            Name = string.Empty,
            Topic = (string?)null,
            ChannelType = 0,
            ParentId = (long?)null,
            IsPresent = false,
            CurrentState = "Inaccessible",
            UpdatedOn = DateTimeOffset.UtcNow,
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.IsPresent.ShouldBeFalse();
        saga.CurrentState.ShouldBe("CaughtUp");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(FakeTimeProvider clock)
    {
        return new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<ChannelSagaStateMachine, ChannelSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);
    }
}
