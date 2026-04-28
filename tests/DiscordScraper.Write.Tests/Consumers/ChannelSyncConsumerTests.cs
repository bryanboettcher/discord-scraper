using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Discord;
using DiscordScraper.Discord.Models;
using DiscordScraper.Write.Consumers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class ChannelSyncConsumerTests
{
    private const long ChannelId = 1_000_000_000_000_001L;
    private const long GuildId   = 2_000_000_000_000_001L;

    // ---------------------------------------------------------------------------
    // Happy path: multiple messages → N MessageCaptured + 1 ChannelSyncCompleted
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_WithMessages_PublishesMessageCapturedPerMessageAndSyncCompleted()
    {
        const int messageCount = 3;
        var messages = BuildMessages(ChannelId, GuildId, messageCount, startSnowflake: 100L);

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(messages.ToAsyncEnumerable());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId,
            GuildId   = GuildId,
            CurrentState = "Syncing",
            LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncRequested>())
            .ShouldBeTrue("Consumer should have consumed the ChannelSyncRequested");

        // N MessageCaptured events
        var captured = harness.Published.Select<MessageCaptured>().ToList();
        captured.Count.ShouldBe(messageCount);

        // 1 ChannelSyncCompleted carrying the highest snowflake
        var completed = harness.Published.Select<ChannelSyncCompleted>().ToList();
        completed.Count.ShouldBe(1);

        var syncResult = completed[0].Context.Message;
        syncResult.LastSyncedSnowflake.ShouldBe(messages.Max(m => m.MessageId));
        syncResult.MessageCount.ShouldBe(messageCount);
        // 3 messages < page size (100) → caught up
        syncResult.IsCaughtUpAtLastPoll.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------
    // Empty channel: 0 messages → 0 MessageCaptured + IsCaughtUpAtLastPoll=true,
    //                cursor unchanged from input
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_EmptyChannel_PublishesZeroMessageCapturedAndCaughtUp()
    {
        const long inputCursor = 500L;

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<DiscordMessageRaw>());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId,
            GuildId   = GuildId,
            CurrentState = "Syncing",
            LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = inputCursor,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncRequested>())
            .ShouldBeTrue();

        harness.Published.Select<MessageCaptured>().ShouldBeEmpty(
            "No MessageCaptured should be published for an empty channel");

        var completed = harness.Published.Select<ChannelSyncCompleted>().ToList();
        completed.Count.ShouldBe(1);

        var syncResult = completed[0].Context.Message;
        // Cursor must equal the input when no messages were returned
        syncResult.LastSyncedSnowflake.ShouldBe(inputCursor);
        syncResult.MessageCount.ShouldBe(0);
        syncResult.IsCaughtUpAtLastPoll.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------
    // Exactly page-size messages → IsCaughtUpAtLastPoll=false (more pages available)
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_FullPage_ReportsNotCaughtUp()
    {
        // 100 messages == page size → more history may exist
        const int pageSize = 100;
        var messages = BuildMessages(ChannelId, GuildId, pageSize, startSnowflake: 1L);

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(messages.ToAsyncEnumerable());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncRequested>();

        var completed = harness.Published.Select<ChannelSyncCompleted>().ToList();
        completed.Count.ShouldBe(1);
        completed[0].Context.Message.IsCaughtUpAtLastPoll.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------
    // ChannelSyncCompleted carries the highest snowflake from the batch
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_ReportsHighestSnowflakeAsCursor()
    {
        var messages = new[]
        {
            new DiscordMessageRaw(300L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, "{}"),
            new DiscordMessageRaw(100L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, "{}"),
            new DiscordMessageRaw(500L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, "{}"),
            new DiscordMessageRaw(200L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, "{}"),
        };

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(messages.ToAsyncEnumerable());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncRequested>();

        var syncResult = harness.Published.Select<ChannelSyncCompleted>().Single().Context.Message;
        syncResult.LastSyncedSnowflake.ShouldBe(500L);
    }

    // ---------------------------------------------------------------------------
    // AuthorId and AuthorIsBot stamped from payload (CRITICAL 2)
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_HumanAuthor_StampsAuthorIdAndIsBotFalse()
    {
        const long authorSnowflake = 987_654_321_000_000L;
        var payload = $$$"""{"id":"100","channel_id":"{{{ChannelId}}}","author":{"id":"{{{authorSnowflake}}}","username":"human"},"content":"hello"}""";
        var message = new DiscordMessageRaw(100L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, payload);

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message }.ToAsyncEnumerable());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncRequested>();

        var captured = harness.Published.Select<MessageCaptured>().Single().Context.Message;
        captured.AuthorId.ShouldBe(authorSnowflake);
        captured.AuthorIsBot.ShouldBeFalse();
    }

    [Test]
    public async Task Consume_BotAuthor_StampsAuthorIdAndIsBotTrue()
    {
        const long botSnowflake = 111_222_333_444_000L;
        var payload = $$$"""{"id":"200","channel_id":"{{{ChannelId}}}","author":{"id":"{{{botSnowflake}}}","username":"mybot","bot":true},"content":"beep"}""";
        var message = new DiscordMessageRaw(200L, ChannelId, GuildId, DateTimeOffset.UtcNow, null, payload);

        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message }.ToAsyncEnumerable());

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncRequested>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", LastUpdatedAt = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncRequested>();

        var captured = harness.Published.Select<MessageCaptured>().Single().Context.Message;
        captured.AuthorId.ShouldBe(botSnowflake);
        captured.AuthorIsBot.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyList<DiscordMessageRaw> BuildMessages(
        long channelId, long guildId, int count, long startSnowflake)
    {
        return Enumerable.Range(0, count)
            .Select(i => new DiscordMessageRaw(
                MessageId: startSnowflake + i,
                ChannelId: channelId,
                GuildId: guildId,
                CreatedAt: DateTimeOffset.UtcNow,
                EditedAt: null,
                Payload: "{}"))
            .ToList();
    }

    private static ServiceProvider BuildProvider(IDiscordClient discord)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        return new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton(clock)
            .AddLogging()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<ChannelSyncConsumer, ChannelSyncConsumerDefinition>();
            })
            .BuildServiceProvider(true);
    }
}
