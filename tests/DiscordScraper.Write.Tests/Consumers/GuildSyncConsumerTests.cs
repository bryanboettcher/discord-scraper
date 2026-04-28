using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Discord;
using DiscordScraper.Discord.Models;
using DiscordScraper.Write.Consumers;
using DiscordScraper.Write.Repositories;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class GuildSyncConsumerTests
{
    private const long GuildId = 42L;

    private static readonly DiscordGuildRaw TestGuild = new(GuildId, "Test Guild", "{}");

    private static DiscordChannelRaw MakeChannel(long id, int type = 0) =>
        new(id, GuildId, type, null, $"channel-{id}", "{}");

    // ---------------------------------------------------------------------------
    // Cursor repo returns empty → all channels get cursor=0
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoEmpty_AllChannelsGetZeroCursor()
    {
        var channel1 = MakeChannel(1001L);
        var channel2 = MakeChannel(1002L);

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1, channel2]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", LastUpdatedAt = DateTimeOffset.UtcNow,
        });

        // Wait for both ChannelSyncRequested publishes before asserting counts.
        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue("GuildChanged signals consumer completed");

        var published = harness.Published.Select<ChannelSyncRequested>().ToList();
        published.Count.ShouldBe(2);
        published.ShouldAllBe(p => p.Context.Message.CursorSnowflake == 0L,
            "All channels should get cursor=0 when repo returns empty");
    }

    // ---------------------------------------------------------------------------
    // Cursor repo returns subset → matched channels get their cursor, others get 0
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoPartialMatch_MatchedChannelsGetCursorOthersGetZero()
    {
        var channel1 = MakeChannel(2001L);
        var channel2 = MakeChannel(2002L);
        var channel3 = MakeChannel(2003L);

        const long cursor1 = 999_000_000_000L;

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1, channel2, channel3]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(
                new Dictionary<long, long> { [2001L] = cursor1 })); // only channel1 has a cursor

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", LastUpdatedAt = DateTimeOffset.UtcNow,
        });

        // GuildChanged is the terminal publish — wait for it so all ChannelSyncRequested are queued.
        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var byChannel = harness.Published.Select<ChannelSyncRequested>()
            .ToDictionary(p => p.Context.Message.ChannelId, p => p.Context.Message.CursorSnowflake);

        byChannel[2001L].ShouldBe(cursor1);
        byChannel[2002L].ShouldBe(0L);
        byChannel[2003L].ShouldBe(0L);
    }

    // ---------------------------------------------------------------------------
    // Cursor repo throws → consumer faults, exception propagated as Fault message
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoThrows_ConsumerFaultPublished()
    {
        var channel1 = MakeChannel(3001L);

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Mongo unavailable"));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncRequested>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", LastUpdatedAt = DateTimeOffset.UtcNow,
        });

        // Wait for the fault to be published (consumer threw)
        (await harness.Published.Any<Fault<GuildSyncRequested>>()).ShouldBeTrue(
            "A fault should be published when the cursor repo throws");

        // No channel sync requests published — the exception aborted the publish loop
        harness.Published.Select<ChannelSyncRequested>().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(IDiscordClient discord, IChannelCursorRepo cursorRepo)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        return new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton(cursorRepo)
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                // Omit GuildSyncConsumerDefinition: it configures UseMongoDbOutbox which
                // requires a live Mongo instance not available in the unit test harness.
                cfg.AddConsumer<GuildSyncConsumer>();
            })
            .BuildServiceProvider(true);
    }
}
