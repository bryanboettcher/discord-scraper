using DiscordScraper.TestSupport.Observers;

namespace DiscordScraper.Write.Tests.Observers;

/// <summary>
/// Unit tests for <see cref="TestObservationSink"/> and <see cref="ObservationMetrics"/>.
/// These tests exercise the sink's recording, query, and assertion logic in isolation
/// (no MassTransit plumbing — the harness integration is in <see cref="ObserverHarnessTests"/>).
/// </summary>
[TestFixture]
public sealed class TestObservationSinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Type FakeRequestType = typeof(FakeRequest);
    private static readonly Type FakeResponseType = typeof(FakeResponse);

    private sealed record FakeRequest;
    private sealed record FakeResponse;
    private sealed record OtherRequest;

    // -------------------------------------------------------------------------
    // RecordSend / RecordConsume / GapForRequest
    // -------------------------------------------------------------------------

    [Test]
    public void GapForRequest_MatchedPair_ReturnsDelta()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        var sent = T0;
        var consumed = T0.AddMilliseconds(42);

        sink.RecordSend(id, FakeRequestType, sent);
        sink.RecordConsume(id, FakeResponseType, consumed, consumed.AddMilliseconds(1));

        var gap = sink.GapForRequest(id);

        gap.ShouldBe(TimeSpan.FromMilliseconds(42));
    }

    [Test]
    public void GapForRequest_NoSendRecord_Throws()
    {
        var sink = new TestObservationSink();
        sink.RecordConsume(Guid.NewGuid(), FakeResponseType, T0, T0);

        Should.Throw<InvalidOperationException>(() => sink.GapForRequest(Guid.NewGuid()));
    }

    [Test]
    public void GapForRequest_NoConsumeRecord_Throws()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);

        Should.Throw<InvalidOperationException>(() => sink.GapForRequest(id));
    }

    [Test]
    public void GapForRequest_LastWriteWins_WhenSameIdRecordedTwice()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordSend(id, FakeRequestType, T0.AddSeconds(1)); // overwrite

        sink.RecordConsume(id, FakeResponseType, T0.AddSeconds(1).AddMilliseconds(10), T0.AddSeconds(2));

        // Gap should be PostSentAt(T0+1s) → PreConsumedAt(T0+1s+10ms)
        sink.GapForRequest(id).ShouldBe(TimeSpan.FromMilliseconds(10));
    }

    // -------------------------------------------------------------------------
    // GapsForType
    // -------------------------------------------------------------------------

    [Test]
    public void GapsForType_OnlyMatchingTypeIncluded()
    {
        var sink = new TestObservationSink();

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();

        sink.RecordSend(idA, FakeRequestType, T0);
        sink.RecordConsume(idA, FakeResponseType, T0.AddMilliseconds(10), T0.AddMilliseconds(11));

        sink.RecordSend(idB, FakeRequestType, T0);
        sink.RecordConsume(idB, FakeResponseType, T0.AddMilliseconds(20), T0.AddMilliseconds(21));

        // OtherRequest — should not appear in GapsForType<FakeRequest>
        sink.RecordSend(idC, typeof(OtherRequest), T0);
        sink.RecordConsume(idC, FakeResponseType, T0.AddMilliseconds(999), T0.AddMilliseconds(1000));

        var gaps = sink.GapsForType<FakeRequest>();

        gaps.Count.ShouldBe(2);
        gaps.ShouldContain(TimeSpan.FromMilliseconds(10));
        gaps.ShouldContain(TimeSpan.FromMilliseconds(20));
    }

    [Test]
    public void GapsForType_NoMatchingType_ReturnsEmptyList()
    {
        var sink = new TestObservationSink();
        sink.GapsForType<FakeRequest>().ShouldBeEmpty();
    }

    [Test]
    public void GapsForType_SentButNotConsumed_ExcludedFromList()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        // No consume recorded

        sink.GapsForType<FakeRequest>().ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // AssertNoGapsExceeding
    // -------------------------------------------------------------------------

    [Test]
    public void AssertNoGapsExceeding_AllWithin_DoesNotThrow()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordConsume(id, FakeResponseType, T0.AddMilliseconds(5), T0.AddMilliseconds(6));

        Should.NotThrow(() => sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(10)));
    }

    [Test]
    public void AssertNoGapsExceeding_OneViolation_Throws()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordConsume(id, FakeResponseType, T0.AddMilliseconds(100), T0.AddMilliseconds(101));

        var ex = Should.Throw<AssertionException>(() =>
            sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(10)));

        ex.Message.ShouldContain("FakeRequest");
    }

    [Test]
    public void AssertNoGapsExceeding_SendWithoutConsume_NotViolation()
    {
        // Sends with no consume are not evaluated by AssertNoGapsExceeding —
        // use AssertAllConsumedWithin for the stricter check.
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);

        Should.NotThrow(() => sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(1)));
    }

    // -------------------------------------------------------------------------
    // AssertAllConsumedWithin
    // -------------------------------------------------------------------------

    [Test]
    public void AssertAllConsumedWithin_AllMatchedAndWithin_DoesNotThrow()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordConsume(id, FakeResponseType, T0.AddMilliseconds(5), T0.AddMilliseconds(6));

        Should.NotThrow(() => sink.AssertAllConsumedWithin(TimeSpan.FromMilliseconds(10)));
    }

    [Test]
    public void AssertAllConsumedWithin_UnmatchedSend_Throws()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        // No consume

        var ex = Should.Throw<AssertionException>(() =>
            sink.AssertAllConsumedWithin(TimeSpan.FromMilliseconds(10)));

        ex.Message.ShouldContain("never consumed");
    }

    [Test]
    public void AssertAllConsumedWithin_GapExceeded_Throws()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordConsume(id, FakeResponseType, T0.AddSeconds(5), T0.AddSeconds(6));

        var ex = Should.Throw<AssertionException>(() =>
            sink.AssertAllConsumedWithin(TimeSpan.FromSeconds(1)));

        ex.Message.ShouldContain("FakeRequest");
    }

    // -------------------------------------------------------------------------
    // QueueDwell
    // -------------------------------------------------------------------------

    [Test]
    public void RecordQueueDwell_StoredAndRetrievable()
    {
        var sink = new TestObservationSink();
        var msgId = Guid.NewGuid();
        var enqueued = T0;
        var preConsume = T0.AddMilliseconds(75);

        sink.RecordQueueDwell(msgId, typeof(FakeRequest), enqueued, preConsume);

        sink.QueueDwells.ShouldContainKey(msgId);
        var record = sink.QueueDwells[msgId];
        record.Dwell.ShouldBe(TimeSpan.FromMilliseconds(75));
    }

    // -------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------

    [Test]
    public void Reset_ClearsAllCollections()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordSend(id, FakeRequestType, T0);
        sink.RecordConsume(id, FakeResponseType, T0.AddMilliseconds(5), T0.AddMilliseconds(6));
        sink.RecordQueueDwell(Guid.NewGuid(), FakeRequestType, T0, T0.AddMilliseconds(1));

        sink.Reset();

        sink.Sends.ShouldBeEmpty();
        sink.Consumes.ShouldBeEmpty();
        sink.QueueDwells.ShouldBeEmpty();
    }
}
