using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Discord.Options;
using DiscordScraper.Write.Scheduling;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Scheduling;

[TestFixture]
public sealed class SyncSchedulerServiceTests
{
    private IPublishEndpoint _publish = null!;
    private IServiceScopeFactory _scopeFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _publish = Substitute.For<IPublishEndpoint>();

        // Wire a scope that resolves IPublishEndpoint to the mock.
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPublishEndpoint)).Returns(_publish);

        var asyncScope = Substitute.For<IServiceScope, IAsyncDisposable>();
        asyncScope.ServiceProvider.Returns(provider);

        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(asyncScope);

        // CreateAsyncScope() is an extension method over CreateScope(); the mock
        // needs to return a scope that is also IAsyncDisposable so the using block works.
        // NSubstitute's proxy above satisfies both.
    }

    private SyncSchedulerService BuildService(IEnumerable<string> guildIds)
    {
        var options = Options.Create(new DiscordOptions
        {
            BotToken = "test-token",
            Guilds = guildIds.ToList(),
        });

        return new SyncSchedulerService(
            _scopeFactory,
            options,
            NullLogger<SyncSchedulerService>.Instance);
    }

    [Test]
    public async Task PublishOnceAsync_ZeroGuilds_NothingPublished()
    {
        var sut = BuildService([]);

        await sut.PublishOnceAsync(CancellationToken.None);

        await _publish
            .DidNotReceive()
            .Publish<GuildSyncRequested>(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishOnceAsync_ThreeGuilds_PublishesThreeTimes()
    {
        var sut = BuildService(["111", "222", "333"]);

        await sut.PublishOnceAsync(CancellationToken.None);

        // One Publish<GuildSyncRequested> call per guild.  MT's generic Publish<T>
        // resolves to the non-generic overload on IPublishEndpoint at runtime,
        // so we assert on the typed overload directly.
        await _publish
            .Received(3)
            .Publish<GuildSyncRequested>(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PublishOnceAsync_ThreeGuilds_CorrectGuildIdsPublished()
    {
        var sut = BuildService(["100", "200", "300"]);
        var captured = new List<object>();

        _publish
            .Publish<GuildSyncRequested>(Arg.Do<object>(a => captured.Add(a)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await sut.PublishOnceAsync(CancellationToken.None);

        // MT anonymous objects carry GuildId as a property; inspect via reflection.
        var publishedIds = captured
            .Select(obj => (long)obj.GetType().GetProperty("GuildId")!.GetValue(obj)!)
            .ToHashSet();

        publishedIds.ShouldBe(new HashSet<long> { 100L, 200L, 300L });
    }

    [Test]
    public async Task PublishOnceAsync_InvalidGuildString_SkipsInvalidPublishesValid()
    {
        // "bad" is not parseable; "456" is valid.
        var sut = BuildService(["bad", "456"]);

        await sut.PublishOnceAsync(CancellationToken.None);

        await _publish
            .Received(1)
            .Publish<GuildSyncRequested>(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_StopCalled_ReturnsCleanly()
    {
        var sut = BuildService(["111"]);

        // StartAsync launches ExecuteAsync as a background task; it will be sitting in the
        // 5-second warm-up delay.  StopAsync signals the stoppingToken, which cancels
        // Task.Delay, and ExecuteAsync returns.  The hosted-service contract requires that
        // StopAsync completes without throwing.
        await sut.StartAsync(CancellationToken.None);

        Func<Task> act = async () => await sut.StopAsync(CancellationToken.None);

        await act.ShouldNotThrowAsync();
    }
}
