using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Sagas;

/// <summary>
/// Pin polling behavior of <see cref="ChannelSagaStateMachine"/>:
/// entering CaughtUp schedules the first PinPollDue; PinSetChanged updates PinSetCanonical and
/// reschedules (whether the hash changed or not); multiple sync cycles interleave correctly.
/// </summary>
[TestFixture]
public sealed class ChannelSagaPinPollTests
{
    private const long TestChannelId = 1_000_000_000_000_020L;
    private const long TestGuildId   = 2_000_000_000_000_020L;

    private static Guid ExpectedCorrelationId => DeterministicGuid.FromSnowflake(TestChannelId);

    // ---------------------------------------------------------------------------
    // On reaching CaughtUp, saga sets PinPollScheduleId (schedule is armed)
    // ---------------------------------------------------------------------------

    [Test]
    public async Task OnEnteringCaughtUp_PinPollScheduleIdIsSet()
    {
        var clock = MakeClock();

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        await DriveToCaughtUp(harness);
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        // PinPollScheduleId is populated when MT successfully enqueues the scheduled message.
        saga.PinPollScheduleId.ShouldNotBeNull("Schedule should be armed after entering CaughtUp");
    }

    // ---------------------------------------------------------------------------
    // PinSetChanged with different CanonicalHash updates PinSetCanonical and reschedules
    // ---------------------------------------------------------------------------

    [Test]
    public async Task PinSetChanged_WithNewHash_UpdatesCanonicalAndReschedules()
    {
        var clock = MakeClock();

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        await DriveToCaughtUp(harness);
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        const string newHash = "abc123";

        await harness.Bus.Publish<PinSetChanged>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            CurrentState = "CaughtUp",
            CanonicalHash = newHash,
            PinCount = 2,
            ObservedAt = clock.UtcNow,
            LastUpdatedAt = clock.UtcNow,
        });

        // Wait for PinSetChanged to be consumed by the saga
        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.PinSetCanonical.ShouldBe(newHash);
        // Schedule should still be armed (re-scheduled after handling PinSetChanged)
        saga.PinPollScheduleId.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------
    // PinSetChanged with same CanonicalHash leaves state unchanged but still reschedules
    // ---------------------------------------------------------------------------

    [Test]
    public async Task PinSetChanged_WithSameHash_IsNoOpButReschedules()
    {
        const string stableHash = "deadbeef";
        var clock = MakeClock();

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        await DriveToCaughtUp(harness);
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        // First PinSetChanged: sets the canonical
        await harness.Bus.Publish<PinSetChanged>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", CanonicalHash = stableHash,
            PinCount = 1, ObservedAt = clock.UtcNow, LastUpdatedAt = clock.UtcNow,
        });
        await Task.Delay(300);

        var scheduleIdAfterFirst = sagaHarness.Sagas.Contains(ExpectedCorrelationId)?.PinPollScheduleId;
        scheduleIdAfterFirst.ShouldNotBeNull();

        // Second PinSetChanged: same hash — state unchanged but reschedule fires
        await harness.Bus.Publish<PinSetChanged>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", CanonicalHash = stableHash,
            PinCount = 1, ObservedAt = clock.UtcNow, LastUpdatedAt = clock.UtcNow,
        });
        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.PinSetCanonical.ShouldBe(stableHash, "Canonical should remain unchanged for same-hash poll");
        // Schedule should be re-armed (MT cancels old token and issues a new one)
        saga.PinPollScheduleId.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------
    // Multiple sync cycles work correctly with pin scheduling alongside
    // ---------------------------------------------------------------------------

    [Test]
    public async Task MultipleSyncCycles_PinScheduleRemainsActive()
    {
        var clock = MakeClock();

        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<ChannelSagaStateMachine, ChannelSagaState>();

        // First sync cycle
        await DriveToCaughtUp(harness, cursor: 100L);
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        var scheduleIdFirst = sagaHarness.Sagas.Contains(ExpectedCorrelationId)?.PinPollScheduleId;
        scheduleIdFirst.ShouldNotBeNull("Schedule should be armed after first CaughtUp");

        // Second sync cycle: re-enter Syncing then back to CaughtUp
        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", LastUpdatedAt = clock.UtcNow,
            CursorSnowflake = 100L,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.Syncing, TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId, GuildId = TestGuildId,
            CurrentState = "CaughtUp", LastUpdatedAt = clock.UtcNow,
            Name = "general", ChannelType = 0, ParentId = (long?)null,
            LastSyncedSnowflake = 200L, MessageCount = 5, IsCaughtUpAtLastPoll = true,
        });
        await sagaHarness.Exists(ExpectedCorrelationId, m => m.CaughtUp, TimeSpan.FromSeconds(5));

        var saga = sagaHarness.Sagas.Contains(ExpectedCorrelationId);
        saga.ShouldNotBeNull();
        saga.PinPollScheduleId.ShouldNotBeNull("Schedule should remain active after second CaughtUp");
        // Cursor should advance
        saga.LastSyncedSnowflake.ShouldBe(200L);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ISystemClock MakeClock() =>
        Substitute.For<ISystemClock>().With(c => c.UtcNow.Returns(DateTimeOffset.UtcNow));

    private static async Task DriveToCaughtUp(ITestHarness harness, long cursor = 0L)
    {
        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            CurrentState = "Initial",
            LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = cursor,
        });

        await harness.Bus.Publish<ChannelSyncCompleted>(new
        {
            ChannelId = TestChannelId,
            GuildId = TestGuildId,
            CurrentState = "CaughtUp",
            LastUpdatedAt = DateTimeOffset.UtcNow,
            Name = "general",
            ChannelType = 0,
            ParentId = (long?)null,
            LastSyncedSnowflake = cursor == 0L ? 100L : cursor,
            MessageCount = 3,
            IsCaughtUpAtLastPoll = true,
        });
    }

    private static ServiceProvider BuildProvider(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<ChannelSagaStateMachine, ChannelSagaState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);
    }
}
