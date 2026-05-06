using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Filters;
using MassTransit;
using NSubstitute;

namespace DiscordScraper.Contracts.Tests.Filters;

/// <summary>
/// Unit tests for <see cref="OutboundTimestampFilter{T}"/>.
///
/// Verifies:
/// - Send path stamps Timestamp on IStampable messages when Ticks == 0
/// - Send path skips stamp when Timestamp already set (idempotent)
/// - Publish path stamps Timestamp (this was the bug in the original TimestampFilter which
///   only implemented IFilter&lt;SendContext&lt;T&gt;&gt; despite UsePublishFilter registration)
/// - Publish path skips stamp when already set
/// - Non-IStampable messages pass through without error
///
/// Note: message types are public because NSubstitute's Castle DynamicProxy cannot generate
/// proxies for generic types (ConsumeContext&lt;T&gt;, SendContext&lt;T&gt;) closed over private
/// types when the MassTransit.Abstractions assembly is strong-named.
/// </summary>
[TestFixture]
public sealed class OutboundTimestampFilterTests
{
    private static readonly DateTimeOffset FixedNow = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static ISystemClock MakeClock(DateTimeOffset? now = null)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(now ?? FixedNow);
        return clock;
    }

    // Public — required for Castle DynamicProxy to close ConsumeContext<T>/SendContext<T> over
    // these types when MassTransit.Abstractions is strong-named.
    public sealed record StampableMessage : IStampable
    {
        public string Data { get; init; } = "";
        public DateTimeOffset Timestamp { get; set; }
    }

    public sealed record NonStampableMessage
    {
        public string Data { get; init; } = "";
    }

    // -------------------------------------------------------------------------
    // Send path
    // -------------------------------------------------------------------------

    [Test]
    public async Task Send_StampsTimestamp_WhenTicks0()
    {
        var clock = MakeClock();
        var filter = new OutboundTimestampFilter<StampableMessage>(clock);

        var message = new StampableMessage { Data = "hello" };
        var context = Substitute.For<SendContext<StampableMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<SendContext<StampableMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.Timestamp.ShouldBe(FixedNow, "Timestamp must be set to clock.UtcNow when Ticks == 0");
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Send_SkipsStamp_WhenAlreadyStamped()
    {
        var clock = MakeClock();
        var filter = new OutboundTimestampFilter<StampableMessage>(clock);

        var existingTimestamp = FixedNow.AddHours(-1);
        var message = new StampableMessage { Data = "pre-stamped", Timestamp = existingTimestamp };
        var context = Substitute.For<SendContext<StampableMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<SendContext<StampableMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.Timestamp.ShouldBe(existingTimestamp, "Pre-stamped Timestamp must not be overwritten");
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Send_PassesThrough_WhenMessageNotIStampable()
    {
        var clock = MakeClock();
        var filter = new OutboundTimestampFilter<NonStampableMessage>(clock);

        var message = new NonStampableMessage { Data = "no-stamp" };
        var context = Substitute.For<SendContext<NonStampableMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<SendContext<NonStampableMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        // Must not throw — open-generic constraint requires T : class, not T : IStampable
        Should.NotThrow(async () => await filter.Send(context, next));
        await next.Received(1).Send(context);
    }

    // -------------------------------------------------------------------------
    // Publish path (bug-fix coverage: original filter lacked this overload)
    // -------------------------------------------------------------------------

    [Test]
    public async Task Publish_StampsTimestamp_WhenTicks0()
    {
        var clock = MakeClock();
        var filter = new OutboundTimestampFilter<StampableMessage>(clock);

        var message = new StampableMessage { Data = "publish-me" };
        var context = Substitute.For<PublishContext<StampableMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<PublishContext<StampableMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.Timestamp.ShouldBe(FixedNow,
            "Publish path must stamp Timestamp — this was the bug in the original TimestampFilter");
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Publish_SkipsStamp_WhenAlreadyStamped()
    {
        var clock = MakeClock();
        var filter = new OutboundTimestampFilter<StampableMessage>(clock);

        var existingTimestamp = FixedNow.AddHours(-2);
        var message = new StampableMessage { Data = "pre-stamped-publish", Timestamp = existingTimestamp };
        var context = Substitute.For<PublishContext<StampableMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<PublishContext<StampableMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.Timestamp.ShouldBe(existingTimestamp, "Pre-stamped Timestamp must not be overwritten on publish path");
        await next.Received(1).Send(context);
    }
}
