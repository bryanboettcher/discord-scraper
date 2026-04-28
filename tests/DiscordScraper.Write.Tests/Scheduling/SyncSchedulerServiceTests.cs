using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Discord.Options;
using DiscordScraper.Write.Scheduling;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Scheduling;

[TestFixture]
public sealed class SyncSchedulerServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    private IPublishEndpoint _publish = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private ISystemClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _publish = Substitute.For<IPublishEndpoint>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPublishEndpoint)).Returns(_publish);

        // CreateAsyncScope() is an extension over CreateScope(); the mock scope must also
        // implement IAsyncDisposable so the await using block can call DisposeAsync.
        var asyncScope = Substitute.For<IServiceScope, IAsyncDisposable>();
        asyncScope.ServiceProvider.Returns(provider);

        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(asyncScope);

        _clock = Substitute.For<ISystemClock>();
        _clock.UtcNow.Returns(FixedNow);
    }

    private SyncSchedulerService BuildService(TimeSpan? interval = null)
    {
        var options = Options.Create(new DiscordOptions
        {
            BotToken = "test-token",
            SyncInterval = interval ?? SyncInterval,
        });

        return new SyncSchedulerService(
            _scopeFactory,
            options,
            _clock,
            NullLogger<SyncSchedulerService>.Instance);
    }

    [Test]
    public async Task PublishOnceAsync_publishes_exactly_one_SyncHeartbeat()
    {
        var sut = BuildService();

        await sut.PublishOnceAsync(CancellationToken.None);

        await _publish
            .Received(1)
            .Publish<SyncHeartbeat>(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishOnceAsync_Timestamp_comes_from_clock()
    {
        var sut = BuildService();
        object? captured = null;

        _publish
            .Publish<SyncHeartbeat>(Arg.Do<object>(a => captured = a), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await sut.PublishOnceAsync(CancellationToken.None);

        var timestamp = (DateTimeOffset)captured!.GetType().GetProperty("Timestamp")!.GetValue(captured)!;
        timestamp.ShouldBe(FixedNow);
    }

    [Test]
    public async Task PublishOnceAsync_StaleAfter_equals_Timestamp_minus_SyncInterval()
    {
        var sut = BuildService();
        object? captured = null;

        _publish
            .Publish<SyncHeartbeat>(Arg.Do<object>(a => captured = a), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await sut.PublishOnceAsync(CancellationToken.None);

        var timestamp = (DateTimeOffset)captured!.GetType().GetProperty("Timestamp")!.GetValue(captured)!;
        var staleAfter = (DateTimeOffset)captured.GetType().GetProperty("StaleAfter")!.GetValue(captured)!;

        staleAfter.ShouldBe(timestamp - SyncInterval);
    }

    [Test]
    public async Task PublishOnceAsync_threads_cancellation_token_through()
    {
        var sut = BuildService();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.PublishOnceAsync(token);

        await _publish
            .Received(1)
            .Publish<SyncHeartbeat>(Arg.Any<object>(), token);
    }

    [Test]
    public async Task ExecuteAsync_StopCalled_ReturnsCleanly()
    {
        var sut = BuildService();

        // StartAsync launches ExecuteAsync, which blocks in the 5-second warm-up delay.
        // StopAsync signals the stoppingToken, cancelling Task.Delay and unwinding cleanly.
        await sut.StartAsync(CancellationToken.None);

        Func<Task> act = async () => await sut.StopAsync(CancellationToken.None);

        await act.ShouldNotThrowAsync();
    }
}
