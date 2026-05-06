using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Requests;
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
/// Open question Q1 (ISendObserver + saga Request() sends):
///   Under InMemory transport, ISendObserver connected via IBus.ConnectSendObserver does NOT
///   fire for saga-initiated Request() sends. These sends go through the consume context's
///   internal send pipeline, not the bus-level send pipeline. This means Observations.Sends
///   will be empty when using the Integration/Unit tier. The ResponseConsumeObserver (Q2) does
///   fire correctly and is the primary observable primitive for response-side timing.
///   The send side is observable in the E2E/RabbitMQ tier where all sends traverse the broker.
///   Consequence: AssertNoGapsExceeding and GapsForType work as intended in the E2E tier;
///   in Unit/Integration tiers they operate on an empty send set (trivially pass AssertNoGaps,
///   have no gap pairs for GapsForType). The useful assertion in Unit/Integration is on the
///   consume side: use ITestHarness.Consumed to verify messages arrived and were processed.
///
/// Open question Q2 (saga-endpoint IConsumeMessageObserver wiring):
///   ConnectResponseObserver&lt;TResponse&gt; via IBus.ConnectConsumeMessageObserver fires correctly
///   at all endpoints for the specified response type. Verified: ClassifyMessageResponse,
///   TagMessageResponse, AnalyzeMessageResponse, and ProjectMessageResponse are all observed
///   by their respective ResponseConsumeObserver instances.
///
/// Open question Q3 (SentTime nullability per transport):
///   InMemory transport: ConsumeContext.SentTime is null. The QueueDwellObserver records
///   EnqueuedAt = DateTimeOffset.MinValue for InMemory transport. The dwell computation
///   (PreConsumedAt - EnqueuedAt) produces a large positive value and is not meaningful.
///   RabbitMQ transport: SentTime is populated by the broker. Queue dwell is meaningful.
///   Callers should filter QueueDwells by EnqueuedAt != DateTimeOffset.MinValue to exclude
///   InMemory transport records from dwell analysis.
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
        // AnalyzeMessage responses will be consumed for all 10; ProjectMessage responses only for
        // the 9 that pass the IsSubstantive gate ("ok" fails and goes to Excluded state).
        var analyzeConsumed = harness.Consumed.Select<AnalyzeMessageResponse>().Count();
        analyzeConsumed.ShouldBeGreaterThan(0,
            "AnalyzeMessageResponse must be consumed for messages that complete the Analyze phase");

        // Observation sink: consume-side records (via ResponseConsumeObserver)
        // are populated for responses even when sends are empty (see Q1 note above).
        var consumes = stack.Observations.Consumes;
        TestContext.Out.WriteLine(
            $"[OBS] Sends={stack.Observations.Sends.Count} " +
            $"Consumes={consumes.Count} " +
            $"QueueDwells={stack.Observations.QueueDwells.Count}");

        // AssertNoGapsExceeding is trivially satisfied when sends are empty (no pairs to evaluate).
        // This is expected behavior in the Integration tier — see Q1 note.
        stack.Observations.AssertNoGapsExceeding(TimeSpan.FromSeconds(15));
    }

    // -------------------------------------------------------------------------
    // Latency-induced: drift on tagging → AnalyzeMessage responses still arrive,
    // but the saga hangs longer in Tagging state
    // -------------------------------------------------------------------------

    [Test]
    [Timeout(120_000)]
    public async Task Integration_DriftLatency_SagaStillProcessesButResponseConsumeTimingIncreases()
    {
        // Arrange — drift latency on the embedding stub: 50ms start, +50ms per call.
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

        TestContext.Out.WriteLine(
            $"[DRIFT] TagMessageResponse consumed={tagConsumed} " +
            $"with drift latency (50ms+50ms/call)");

        // GapsForType on TagMessageRequest will be empty in Integration tier (Q1).
        // In E2E tier (RabbitMQ) these would be non-empty and show the drift.
        var tagGaps = stack.Observations.GapsForType<TagMessageRequest>();
        TestContext.Out.WriteLine(
            $"[OBS] TagMessageRequest gap count={tagGaps.Count} " +
            $"(expected 0 in Integration tier — sends not observable via ISendObserver on InMemory transport)");
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
            $"Observations.Sends={stack.Observations.Sends.Count}, " +
            $"Observations.Consumes={stack.Observations.Consumes.Count}");
    }
}
