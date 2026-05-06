using System.Collections.Concurrent;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Accumulates timestamped observation events from MT send/consume observers so tests
/// can measure the gap between request publish and response consume.
///
/// All methods are thread-safe; observer callbacks fire from concurrent MT dispatch threads.
///
/// Terminology:
///   "request pair"  — a matched (PostSend, PreConsume) tuple joined on RequestId.
///   "queue dwell"   — time a message spent waiting in the broker queue before consume started.
///   "gap"           — the observable superset of queue dwell plus any consumer processing before
///                     PreConsume fires; in practice it equals PostSend → PreConsume delta.
/// </summary>
public interface ITestObservationSink
{
    // -------------------------------------------------------------------------
    // Write side — called by observers
    // -------------------------------------------------------------------------

    /// <summary>Records the PostSend timestamp for a request-type message.</summary>
    void RecordSend(Guid requestId, Type messageType, DateTimeOffset timestamp);

    /// <summary>Records the PreConsume and PostConsume timestamps for a response-type message.</summary>
    void RecordConsume(Guid requestId, Type messageType, DateTimeOffset preConsumeAt, DateTimeOffset postConsumeAt);

    /// <summary>
    /// Records a general consume event keyed on MessageId (not RequestId).
    /// Used for read-side queue-dwell visibility where messages are not request-response pairs.
    /// </summary>
    void RecordQueueDwell(Guid messageId, Type messageType, DateTimeOffset enqueuedAt, DateTimeOffset preConsumeAt);

    // -------------------------------------------------------------------------
    // Query side — used by test assertions
    // -------------------------------------------------------------------------

    /// <summary>All recorded send events, keyed by RequestId.</summary>
    IReadOnlyDictionary<Guid, SendRecord> Sends { get; }

    /// <summary>All recorded consume events, keyed by RequestId.</summary>
    IReadOnlyDictionary<Guid, ConsumeRecord> Consumes { get; }

    /// <summary>All recorded queue-dwell events, keyed by MessageId.</summary>
    IReadOnlyDictionary<Guid, QueueDwellRecord> QueueDwells { get; }

    /// <summary>
    /// PostSend → PreConsume delta for a single request.
    /// Throws <see cref="InvalidOperationException"/> if either side is not yet recorded.
    /// </summary>
    TimeSpan GapForRequest(Guid requestId);

    /// <summary>PostSend → PreConsume deltas for every recorded pair whose send type is <typeparamref name="TRequest"/>.</summary>
    IReadOnlyList<TimeSpan> GapsForType<TRequest>() where TRequest : class;

    /// <summary>
    /// Fails the test if any recorded request-response gap exceeds <paramref name="threshold"/>.
    /// Gaps are only evaluated for pairs where both a send and a consume have been recorded.
    /// </summary>
    void AssertNoGapsExceeding(TimeSpan threshold);

    /// <summary>
    /// Fails the test if any request that was recorded as sent does not have a corresponding
    /// consume record, or if its gap exceeds <paramref name="threshold"/>.
    /// </summary>
    void AssertAllConsumedWithin(TimeSpan threshold);
}

/// <summary>A single PostSend observation for a request-type message.</summary>
public sealed record SendRecord(Guid RequestId, Type MessageType, DateTimeOffset PostSentAt);

/// <summary>A single PreConsume+PostConsume observation for a response-type message.</summary>
public sealed record ConsumeRecord(Guid RequestId, Type MessageType, DateTimeOffset PreConsumedAt, DateTimeOffset PostConsumedAt);

/// <summary>A queue-dwell observation for a non-request-response message (e.g. read-side events).</summary>
public sealed record QueueDwellRecord(Guid MessageId, Type MessageType, DateTimeOffset EnqueuedAt, DateTimeOffset PreConsumedAt)
{
    public TimeSpan Dwell => PreConsumedAt - EnqueuedAt;
}
