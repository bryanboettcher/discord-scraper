using DiscordScraper.TestSupport.Observers;

namespace DiscordScraper.Write.Tests.Observers;

/// <summary>
/// Unit tests for <see cref="TestObservationSink"/> and <see cref="ObservationMetrics"/>.
/// These tests exercise the sink's recording, query, and assertion logic in isolation
/// (no MassTransit plumbing — the harness integration is in <see cref="ObserverHarnessTests"/>).
///
/// Gap model: a <c>ConsumeRecord</c> carries both <c>PublishedAt</c> (from the stamped message
/// body) and <c>PreConsumedAt</c> (from <c>ISystemClock.UtcNow</c> at consume time). The gap is
/// <c>PreConsumedAt - PublishedAt</c>. No separate send record is needed.
/// </summary>
[TestFixture]
public sealed class TestObservationSinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Type FakeResponseType = typeof(FakeResponse);
    private static readonly Type OtherResponseType = typeof(OtherResponse);

    private sealed record FakeResponse;
    private sealed record OtherResponse;

    // -------------------------------------------------------------------------
    // RecordConsume / Gap
    // -------------------------------------------------------------------------

    [Test]
    public void RecordConsume_StoredAndGapCorrect()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        var publishedAt = T0;
        var preConsumeAt = T0.AddMilliseconds(42);

        sink.RecordConsume(id, FakeResponseType, publishedAt, preConsumeAt);

        sink.Consumes.ShouldContainKey(id);
        sink.Consumes[id].Gap.ShouldBe(TimeSpan.FromMilliseconds(42));
    }

    [Test]
    public void RecordConsume_LastWriteWins_WhenSameIdRecordedTwice()
    {
        var sink = new TestObservationSink();
        var id = Guid.NewGuid();
        sink.RecordConsume(id, FakeResponseType, T0, T0.AddMilliseconds(100));
        sink.RecordConsume(id, FakeResponseType, T0.AddSeconds(1), T0.AddSeconds(1).AddMilliseconds(10));

        sink.Consumes[id].Gap.ShouldBe(TimeSpan.FromMilliseconds(10));
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

        sink.RecordConsume(idA, FakeResponseType, T0, T0.AddMilliseconds(10));
        sink.RecordConsume(idB, FakeResponseType, T0, T0.AddMilliseconds(20));
        // OtherResponse — should not appear in GapsForType<FakeResponse>
        sink.RecordConsume(idC, OtherResponseType, T0, T0.AddMilliseconds(999));

        var gaps = sink.GapsForType<FakeResponse>();

        gaps.Count.ShouldBe(2);
        gaps.ShouldContain(TimeSpan.FromMilliseconds(10));
        gaps.ShouldContain(TimeSpan.FromMilliseconds(20));
    }

    [Test]
    public void GapsForType_NoMatchingType_ReturnsEmptyList()
    {
        var sink = new TestObservationSink();
        sink.GapsForType<FakeResponse>().ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // AssertNoGapsExceeding
    // -------------------------------------------------------------------------

    [Test]
    public void AssertNoGapsExceeding_AllWithin_DoesNotThrow()
    {
        var sink = new TestObservationSink();
        sink.RecordConsume(Guid.NewGuid(), FakeResponseType, T0, T0.AddMilliseconds(5));

        Should.NotThrow(() => sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(10)));
    }

    [Test]
    public void AssertNoGapsExceeding_OneViolation_Throws()
    {
        var sink = new TestObservationSink();
        sink.RecordConsume(Guid.NewGuid(), FakeResponseType, T0, T0.AddMilliseconds(100));

        var ex = Should.Throw<AssertionException>(() =>
            sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(10)));

        ex.Message.ShouldContain("FakeResponse");
    }

    [Test]
    public void AssertNoGapsExceeding_EmptySink_DoesNotThrow()
    {
        var sink = new TestObservationSink();
        Should.NotThrow(() => sink.AssertNoGapsExceeding(TimeSpan.FromMilliseconds(1)));
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

        sink.RecordQueueDwell(msgId, typeof(FakeResponse), enqueued, preConsume);

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
        sink.RecordConsume(Guid.NewGuid(), FakeResponseType, T0, T0.AddMilliseconds(5));
        sink.RecordQueueDwell(Guid.NewGuid(), FakeResponseType, T0, T0.AddMilliseconds(1));

        sink.Reset();

        sink.Consumes.ShouldBeEmpty();
        sink.QueueDwells.ShouldBeEmpty();
    }
}
