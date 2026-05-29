using DiscordScraper.Contracts.Events.Channel;
using Microsoft.Extensions.Time.Testing;
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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId,
            GuildId   = GuildId,
            CurrentState = "Syncing",
            UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncDue>())
            .ShouldBeTrue("Consumer should have consumed the ChannelSyncDue");

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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId,
            GuildId   = GuildId,
            CurrentState = "Syncing",
            UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = inputCursor,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncDue>())
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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncDue>();

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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncDue>();

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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncDue>();

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

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        await consumerHarness.Consumed.Any<ChannelSyncDue>();

        var captured = harness.Published.Select<MessageCaptured>().Single().Context.Message;
        captured.AuthorId.ShouldBe(botSnowflake);
        captured.AuthorIsBot.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------
    // Discord 403 → ChannelChanged(IsPresent=false) + ChannelSyncCompleted, no fault
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_Discord403_PublishesChannelChangedNotPresentAndSyncCompleted_NoFault()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(x => ThrowHttpException(System.Net.HttpStatusCode.Forbidden));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncDue>()).ShouldBeTrue();

        // Must publish ChannelChanged with IsPresent=false
        var changed = harness.Published.Select<ChannelChanged>().ToList();
        changed.Count.ShouldBe(1);
        changed[0].Context.Message.IsPresent.ShouldBeFalse();

        // Must also publish ChannelSyncCompleted so saga can transition to CaughtUp
        harness.Published.Select<ChannelSyncCompleted>().Count().ShouldBe(1);

        // No MessageCaptured, no Fault
        harness.Published.Select<MessageCaptured>().ShouldBeEmpty();
        harness.Published.Select<Fault<ChannelSyncDue>>().ShouldBeEmpty();
    }

    [Test]
    public async Task Consume_Discord404_PublishesChannelChangedNotPresentAndSyncCompleted_NoFault()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord
            .EnumerateChannelMessagesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(x => ThrowHttpException(System.Net.HttpStatusCode.NotFound));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<ChannelSyncDue>(new
        {
            ChannelId = ChannelId, GuildId = GuildId,
            CurrentState = "Syncing", UpdatedOn = DateTimeOffset.UtcNow,
            CursorSnowflake = 0L,
        });

        var consumerHarness = harness.GetConsumerHarness<ChannelSyncConsumer>();
        (await consumerHarness.Consumed.Any<ChannelSyncDue>()).ShouldBeTrue();

        var changed = harness.Published.Select<ChannelChanged>().ToList();
        changed.Count.ShouldBe(1);
        changed[0].Context.Message.IsPresent.ShouldBeFalse();
        harness.Published.Select<ChannelSyncCompleted>().Count().ShouldBe(1);
        harness.Published.Select<Fault<ChannelSyncDue>>().ShouldBeEmpty();
    }

    // Produces an IAsyncEnumerable that immediately throws the given HTTP exception.
    private static IAsyncEnumerable<DiscordMessageRaw> ThrowHttpException(System.Net.HttpStatusCode statusCode) =>
        ThrowingAsyncEnumerable<DiscordMessageRaw>(
            new System.Net.Http.HttpRequestException(
                $"Discord {(int)statusCode}", inner: null, statusCode: statusCode));

    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception ex)
    {
        await Task.CompletedTask;
        throw ex;
        yield return default!; // satisfies the iterator contract; never reached
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
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        return new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton<TimeProvider>(clock)
            .AddLogging()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<ChannelSyncConsumer, ChannelSyncConsumerDefinition>();
            })
            .BuildServiceProvider(true);
    }
}
