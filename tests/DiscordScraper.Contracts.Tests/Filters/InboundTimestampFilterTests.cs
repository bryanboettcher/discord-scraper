using DiscordScraper.Contracts.Filters;
using Microsoft.Extensions.Time.Testing;
using MassTransit;
using NSubstitute;

namespace DiscordScraper.Contracts.Tests.Filters;

/// <summary>
/// Unit tests for <see cref="InboundTimestampFilter{T}"/>.
///
/// Verifies:
/// - Stamps ReceivedOn on IMeasured messages when Ticks == 0
/// - Skips stamp when ReceivedOn already set (idempotent)
/// - TransportLatency is correct and non-negative after both fields are stamped
/// - Non-IMeasured messages pass through without error
///
/// Note: message types are public because NSubstitute's Castle DynamicProxy cannot generate
/// proxies for generic types (ConsumeContext&lt;T&gt;) closed over private types when the
/// MassTransit.Abstractions assembly is strong-named.
/// </summary>
[TestFixture]
public sealed class InboundTimestampFilterTests
{
    private static readonly DateTimeOffset PublishedAt = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReceivedAt = new(2024, 1, 15, 12, 0, 0, 250, TimeSpan.Zero); // 250ms later

    private static FakeTimeProvider MakeClock(DateTimeOffset? now = null)
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(now ?? ReceivedAt);
        return clock;
    }

    // Public — required for Castle DynamicProxy to close ConsumeContext<T> over these types
    // when MassTransit.Abstractions is strong-named.
    public sealed record MeasuredMessage : IMeasured
    {
        public string Data { get; init; } = "";
        public DateTimeOffset Timestamp { get; set; }
        public DateTimeOffset ReceivedOn { get; set; }
    }

    public sealed record StampableOnlyMessage : IStampable
    {
        public string Data { get; init; } = "";
        public DateTimeOffset Timestamp { get; set; }
    }

    public sealed record PlainMessage
    {
        public string Data { get; init; } = "";
    }

    // -------------------------------------------------------------------------
    // IMeasured: stamps ReceivedOn
    // -------------------------------------------------------------------------

    [Test]
    public async Task Send_StampsReceivedOn_WhenTicks0()
    {
        var clock = MakeClock(ReceivedAt);
        var filter = new InboundTimestampFilter<MeasuredMessage>(clock);

        var message = new MeasuredMessage { Data = "incoming", Timestamp = PublishedAt };
        var context = Substitute.For<ConsumeContext<MeasuredMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<ConsumeContext<MeasuredMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.ReceivedOn.ShouldBe(ReceivedAt, "ReceivedOn must be set to clock.UtcNow when Ticks == 0");
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Send_SkipsStamp_WhenAlreadyStamped()
    {
        var clock = MakeClock(ReceivedAt);
        var filter = new InboundTimestampFilter<MeasuredMessage>(clock);

        var existingReceivedOn = ReceivedAt.AddMilliseconds(-50);
        var message = new MeasuredMessage { Data = "pre-received", Timestamp = PublishedAt, ReceivedOn = existingReceivedOn };
        var context = Substitute.For<ConsumeContext<MeasuredMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<ConsumeContext<MeasuredMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        message.ReceivedOn.ShouldBe(existingReceivedOn, "Pre-stamped ReceivedOn must not be overwritten");
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Send_TransportLatency_CorrectAfterBothStamped()
    {
        var clock = MakeClock(ReceivedAt);
        var filter = new InboundTimestampFilter<MeasuredMessage>(clock);

        // Simulate: outbound filter already set Timestamp; inbound filter sets ReceivedOn
        var message = new MeasuredMessage { Data = "latency-test", Timestamp = PublishedAt };
        var context = Substitute.For<ConsumeContext<MeasuredMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<ConsumeContext<MeasuredMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        await filter.Send(context, next);

        var expected = ReceivedAt - PublishedAt; // 250ms
        ((IMeasured)message).TransportLatency.ShouldBe(expected,
            "TransportLatency must equal ReceivedOn - Timestamp");
        ((IMeasured)message).TransportLatency.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero,
            "TransportLatency must be non-negative");
    }

    // -------------------------------------------------------------------------
    // Non-IMeasured messages must pass through without error
    // -------------------------------------------------------------------------

    [Test]
    public async Task Send_PassesThrough_WhenMessageIsIStampableButNotIMeasured()
    {
        var clock = MakeClock();
        var filter = new InboundTimestampFilter<StampableOnlyMessage>(clock);

        var message = new StampableOnlyMessage { Data = "stampable", Timestamp = PublishedAt };
        var context = Substitute.For<ConsumeContext<StampableOnlyMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<ConsumeContext<StampableOnlyMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        // Must not throw — filter only acts on IMeasured, skips IStampable-only
        Should.NotThrow(async () => await filter.Send(context, next));
        await next.Received(1).Send(context);
    }

    [Test]
    public async Task Send_PassesThrough_WhenMessageNotIMeasured()
    {
        var clock = MakeClock();
        var filter = new InboundTimestampFilter<PlainMessage>(clock);

        var message = new PlainMessage { Data = "plain" };
        var context = Substitute.For<ConsumeContext<PlainMessage>>();
        context.Message.Returns(message);

        var next = Substitute.For<IPipe<ConsumeContext<PlainMessage>>>();
        next.Send(context).Returns(Task.CompletedTask);

        // Must not throw — open-generic constraint requires T : class, not T : IMeasured
        Should.NotThrow(async () => await filter.Send(context, next));
        await next.Received(1).Send(context);
    }
}
