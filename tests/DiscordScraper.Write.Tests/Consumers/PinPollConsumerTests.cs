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
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class PinPollConsumerTests
{
    private const long ChannelId = 1_000_000_000_000_010L;
    private const long GuildId   = 2_000_000_000_000_010L;

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EditedAt  = new(2026, 1, 19, 14, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------------
    // Empty pin response → PinSetChanged(PinCount=0), no MessageEditObserved
    // ---------------------------------------------------------------------------

    [Test]
    public async Task EmptyPins_PublishesPinSetChangedWithCountZero_AndNoEditEvents()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetChannelPinsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordChannelPins("[]", []));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<PinPollDue>(MakePinPollDue());
        var consumerHarness = harness.GetConsumerHarness<PinPollConsumer>();
        (await consumerHarness.Consumed.Any<PinPollDue>()).ShouldBeTrue();

        var pinSetChanged = harness.Published.Select<PinSetChanged>().ToList();
        pinSetChanged.Count.ShouldBe(1);
        pinSetChanged[0].Context.Message.PinCount.ShouldBe(0);
        pinSetChanged[0].Context.Message.CanonicalHash.ShouldNotBeNullOrEmpty();

        harness.Published.Select<MessageEditObserved>().ShouldBeEmpty(
            "No MessageEditObserved should be published when no pins are returned");
    }

    // ---------------------------------------------------------------------------
    // 3 pins, none edited → PinSetChanged + 0 MessageEditObserved
    // ---------------------------------------------------------------------------

    [Test]
    public async Task ThreePins_NoneEdited_PublishesPinSetChangedOnly()
    {
        var messages = new[]
        {
            MakeMessage(100L, editedAt: null),
            MakeMessage(200L, editedAt: null),
            MakeMessage(300L, editedAt: null),
        };

        var discord = Substitute.For<IDiscordClient>();
        discord.GetChannelPinsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordChannelPins("[]", messages));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<PinPollDue>(MakePinPollDue());
        var consumerHarness = harness.GetConsumerHarness<PinPollConsumer>();
        (await consumerHarness.Consumed.Any<PinPollDue>()).ShouldBeTrue();

        harness.Published.Select<PinSetChanged>().Count().ShouldBe(1);
        harness.Published.Select<PinSetChanged>().Single().Context.Message.PinCount.ShouldBe(3);

        harness.Published.Select<MessageEditObserved>().ShouldBeEmpty(
            "No MessageEditObserved for unedited pins");
    }

    // ---------------------------------------------------------------------------
    // 2 pins both with EditedAt → PinSetChanged + 2 MessageEditObserved
    // ---------------------------------------------------------------------------

    [Test]
    public async Task TwoPins_BothEdited_PublishesPinSetChangedAndTwoEditEvents()
    {
        var messages = new[]
        {
            MakeMessage(100L, editedAt: EditedAt),
            MakeMessage(200L, editedAt: EditedAt.AddHours(1)),
        };

        var discord = Substitute.For<IDiscordClient>();
        discord.GetChannelPinsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new DiscordChannelPins("[]", messages));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<PinPollDue>(MakePinPollDue());
        var consumerHarness = harness.GetConsumerHarness<PinPollConsumer>();
        (await consumerHarness.Consumed.Any<PinPollDue>()).ShouldBeTrue();

        harness.Published.Select<PinSetChanged>().Count().ShouldBe(1);

        var edits = harness.Published.Select<MessageEditObserved>().ToList();
        edits.Count.ShouldBe(2);

        var snowflakes = edits.Select(e => e.Context.Message.MessageSnowflake).ToHashSet();
        snowflakes.ShouldContain(100L);
        snowflakes.ShouldContain(200L);

        // UpdatedPayloadJson should be the raw Discord payload
        foreach (var edit in edits)
            edit.Context.Message.UpdatedPayloadJson.ShouldBe("{}");
    }

    // ---------------------------------------------------------------------------
    // Discord client throws → consumer faults (message moves to error queue)
    // ---------------------------------------------------------------------------

    [Test]
    public async Task DiscordClientThrows_ConsumerFaults()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetChannelPinsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Discord unavailable"));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<PinPollDue>(MakePinPollDue());
        var consumerHarness = harness.GetConsumerHarness<PinPollConsumer>();

        // The consumer should fault; MT TestHarness captures faulted messages
        (await consumerHarness.Consumed.Any<PinPollDue>()).ShouldBeTrue();

        // No events should be published on failure
        harness.Published.Select<PinSetChanged>().ShouldBeEmpty();
        harness.Published.Select<MessageEditObserved>().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // CancellationToken is threaded through to the Discord client
    // ---------------------------------------------------------------------------

    [Test]
    public async Task CancellationToken_IsPassedToDiscordClient()
    {
        CancellationToken capturedToken = default;

        var discord = Substitute.For<IDiscordClient>();
        discord
            .GetChannelPinsAsync(
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Do<CancellationToken>(ct => capturedToken = ct))
            .Returns(new DiscordChannelPins("[]", []));

        await using var provider = BuildProvider(discord);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<PinPollDue>(MakePinPollDue());
        var consumerHarness = harness.GetConsumerHarness<PinPollConsumer>();
        (await consumerHarness.Consumed.Any<PinPollDue>()).ShouldBeTrue();

        // The token captured from the Discord call must not be the default (unset) token;
        // MT's ConsumeContext provides a real CancellationToken.
        capturedToken.ShouldNotBe(default);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static object MakePinPollDue() => new
    {
        ChannelId,
        GuildId,
        CurrentState = "CaughtUp",
        DueAt = FixedNow,
        UpdatedOn = FixedNow,
    };

    private static DiscordMessageRaw MakeMessage(long id, DateTimeOffset? editedAt) =>
        new(id, ChannelId, GuildId, FixedNow, editedAt, "{}");

    private static ServiceProvider BuildProvider(IDiscordClient discord)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(FixedNow);

        return new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton(clock)
            .AddLogging()
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<PinPollConsumer>();
            })
            .BuildServiceProvider(true);
    }
}
