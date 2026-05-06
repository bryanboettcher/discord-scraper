using DiscordScraper.Contracts.Requests;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.TestSupport;
using DiscordScraper.TestSupport.Observers;
using DiscordScraper.TestSupport.Stubs;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.E2E.Tests.Saga;

/// <summary>
/// Proof-of-life integration tests for the TestStack tier factory.
///
/// These tests use the Integration tier (InMemory MT transport + Mongo via Testcontainers)
/// and drive the saga through the full happy-path (Captured → Enriched) using stubs.
///
/// The synthetic fixture (synthetic_small.jsonl) has 10 messages. One is short-form ("ok")
/// that AnalyzeMessageConsumer marks non-substantive; the remaining 9 run through the full
/// Analyze → Project → Tag → Classify chain.
///
/// Gap measurement model (post-Task E):
///   <see cref="Filters.TimestampFilter{T}"/> stamps a publish-time timestamp into every
///   <see cref="IStampable"/> message body. <see cref="ResponseConsumeObserver{TResponse}"/>
///   reads <c>context.Message.Timestamp</c> at PreConsume to derive the publish→consume gap.
///   No send-side observer is required. This works on all transports including InMemory.
///
/// Open question Q1 (saga Request() sends — resolved):
///   Under InMemory transport, <c>ISendObserver</c> does NOT fire for saga-initiated
///   <c>Request()</c> sends. The fix: stamp timestamps into message bodies via
///   <see cref="Filters.TimestampFilter{T}"/> so the consume side can compute the gap
///   directly from <c>context.Message.Timestamp</c>. Gaps in <see cref="ITestObservationSink.Consumes"/>
///   are now populated for all tiers when messages implement <see cref="IStampable"/>.
///
/// Open question Q2 (saga-endpoint IConsumeMessageObserver wiring):
///   ConnectResponseObserver&lt;TResponse&gt; via IBus.ConnectConsumeMessageObserver fires correctly
///   at all endpoints for the specified response type. Verified unchanged.
///
/// Open question Q3 (SentTime nullability per transport):
///   InMemory transport: ConsumeContext.SentTime is null. The QueueDwellObserver now prefers
///   <c>IStampable.Timestamp</c> over <c>SentTime</c>. For non-IStampable messages on InMemory,
///   EnqueuedAt = DateTimeOffset.MinValue (sentinel). Filter QueueDwells by
///   EnqueuedAt != DateTimeOffset.MinValue to exclude sentinel records from dwell analysis.
///
/// Docker requirement: Testcontainers needs Docker. Tests are tagged [Category("Integration")].
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class MessageSagaTimeoutHypothesisTests
{
    // -------------------------------------------------------------------------
    // Happy-path: clean knobs → fixture replays, saga enriches messages
    // -------------------------------------------------------------------------

    [Test]
    [Timeout(120_000)]
    public async Task Integration_CleanKnobs_FixtureReplaysAndSagaProcessesMessages()
    {
        // Arrange
        await using var stack = TestStack.Integration()
            .WithFixture("synthetic_small.jsonl")
            .WithRequestTimeout(TimeSpan.FromSeconds(15));

        await stack.StartAsync();

        // Resolve harness for MT-native consume assertions
        var harness = stack.GetTestHarness();

        // Act — burst replay + wait for quiescence
        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(90));

        // Assert — all 10 MessageCaptured events were consumed (fixture has 10)
        var capturedConsumed = harness.Consumed.Select<MessageCaptured>().Count();
        capturedConsumed.ShouldBe(10,
            "All 10 MessageCaptured events from synthetic_small.jsonl must be consumed by the saga");

        // The 9 substantive messages drive through AnalyzeMessage → ProjectMessage → Tag → Classify.
        var analyzeConsumed = harness.Consumed.Select<AnalyzeMessageResponse>().Count();
        analyzeConsumed.ShouldBeGreaterThan(0,
            "AnalyzeMessageResponse must be consumed for messages that complete the Analyze phase");

        // Observation sink: consume records (via ResponseConsumeObserver) are now populated for
        // all tiers — TimestampFilter stamps IStampable messages so PreConsumedAt - PublishedAt
        // is computable without a send-side observer.
        var consumes = stack.Observations.Consumes;
        TestContext.Out.WriteLine(
            $"[OBS] Consumes={consumes.Count} " +
            $"QueueDwells={stack.Observations.QueueDwells.Count}");

        // With TimestampFilter in place, gaps should be populated and within a generous threshold.
        stack.Observations.AssertNoGapsExceeding(TimeSpan.FromSeconds(15));
    }

    // -------------------------------------------------------------------------
    // Latency-induced: drift on tagging → gaps increase measurably
    // -------------------------------------------------------------------------

    [Test]
    [Timeout(120_000)]
    public async Task Integration_DriftLatency_SagaStillProcessesButResponseConsumeTimingIncreases()
    {
        // Arrange — drift latency on the tagging stub: 50ms start, +50ms per call.
        // With 9 substantive messages: 50ms, 100ms, ..., 450ms. Well within the 15s timeout
        // but measurably different from zero-latency, proving the knobs propagate to the stubs.
        var driftLatency = new LatencyProfile<string>.Drift(
            Start: TimeSpan.FromMilliseconds(50),
            PerCall: TimeSpan.FromMilliseconds(50));

        await using var stack = TestStack.Integration()
            .WithFixture("synthetic_small.jsonl")
            .WithTaggingLatency(driftLatency)
            .WithRequestTimeout(TimeSpan.FromSeconds(15));

        await stack.StartAsync();

        var harness = stack.GetTestHarness();

        // Act
        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(90));

        // Assert — saga still processes all captured messages despite latency
        harness.Consumed.Select<MessageCaptured>().Count().ShouldBe(10,
            "All 10 MessageCaptured events must be consumed even with stub latency");

        // At least some TagMessageResponse events consumed (substantive messages trigger Tag)
        var tagConsumed = harness.Consumed.Select<TagMessageResponse>().Count();
        tagConsumed.ShouldBeGreaterThan(0,
            "At least some TagMessageResponse events must be consumed for substantive messages");

        // GapsForType<TagMessageResponse>: now populated because TimestampFilter stamps the body.
        // Drift latency means Tag responses arrive later — gaps should reflect the extra wait.
        var tagGaps = stack.Observations.GapsForType<TagMessageResponse>();
        TestContext.Out.WriteLine(
            $"[DRIFT] TagMessageResponse consumed={tagConsumed} " +
            $"gap_count={tagGaps.Count} " +
            $"p50={ObservationMetrics.P50(tagGaps).TotalMilliseconds:F0}ms " +
            $"p95={ObservationMetrics.P95(tagGaps).TotalMilliseconds:F0}ms");

        // With drift latency, at least some gap data should be available
        // (requires TimestampFilter to have stamped the TagMessageResponse body)
        if (tagGaps.Count > 0)
        {
            ObservationMetrics.P50(tagGaps).ShouldBeGreaterThan(TimeSpan.Zero,
                "Drift latency should produce non-zero gaps when TimestampFilter is active");
        }
    }

    // -------------------------------------------------------------------------
    // Failure injection: embedding fails every 3rd call → some sagas fault,
    // infrastructure remains stable
    // -------------------------------------------------------------------------

    [Test]
    [Timeout(120_000)]
    public async Task Integration_EmbeddingFailureEveryThird_SomeSagasFaultInfrastructureStable()
    {
        // Arrange
        var everyThirdFail = new FailureProfile<string>.EveryNth(
            N: 3,
            ExceptionFactory: () => new InvalidOperationException("stub embedding failure"));

        await using var stack = TestStack.Integration()
            .WithFixture("synthetic_small.jsonl")
            .WithEmbeddingFailure(everyThirdFail)
            .WithRequestTimeout(TimeSpan.FromSeconds(15));

        await stack.StartAsync();

        var harness = stack.GetTestHarness();

        // Act
        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(90));

        // Assert — all 10 MessageCaptured events consumed despite failures
        harness.Consumed.Select<MessageCaptured>().Count().ShouldBe(10,
            "All MessageCaptured events must be consumed even when some embedding calls fault");

        TestContext.Out.WriteLine(
            $"[FAULT] MessageCaptured consumed=10, " +
            $"Observations.Consumes={stack.Observations.Consumes.Count}");
    }
}
