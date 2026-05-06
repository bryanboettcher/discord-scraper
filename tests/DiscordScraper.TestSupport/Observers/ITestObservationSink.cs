using System.Collections.Concurrent;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Accumulates timestamped observation events from MT consume observers so tests can measure
/// the gap between request publish (stamped into the message body by
/// <see cref="Filters.TimestampFilter{T}"/>) and response consume.
///
/// All methods are thread-safe; observer callbacks fire from concurrent MT dispatch threads.
///
/// Terminology:
///   "gap"        — <c>PublishedAt → PreConsumedAt</c>; derived entirely from
///                  <c>context.Message.Timestamp</c> (publish moment) and <c>clock.UtcNow</c>
///                  at PreConsume. No separate send-side observer needed.
///   "queue dwell" — enqueued-at (from <c>IStampable.Timestamp</c> or broker <c>SentTime</c>)
///                  to PreConsume; meaningful on transports that populate these signals.
/// </summary>
public interface ITestObservationSink
{
    // -------------------------------------------------------------------------
    // Write side — called by observers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records the gap between publish (<paramref name="publishedAt"/> from
    /// <c>IStampable.Timestamp</c>) and consume (<paramref name="preConsumeAt"/> from
    /// <c>clock.UtcNow</c> at PreConsume) for a response-type message.
    /// </summary>
    void RecordConsume(Guid requestId, Type messageType, DateTimeOffset publishedAt, DateTimeOffset preConsumeAt);

    /// <summary>
    /// Records a general consume event keyed on MessageId (not RequestId).
    /// Used for read-side queue-dwell visibility where messages are not request-response pairs.
    /// </summary>
    void RecordQueueDwell(Guid messageId, Type messageType, DateTimeOffset enqueuedAt, DateTimeOffset preConsumeAt);

    // -------------------------------------------------------------------------
    // Query side — used by test assertions
    // -------------------------------------------------------------------------

    /// <summary>All recorded consume events, keyed by RequestId.</summary>
    IReadOnlyDictionary<Guid, ConsumeRecord> Consumes { get; }

    /// <summary>All recorded queue-dwell events, keyed by MessageId.</summary>
    IReadOnlyDictionary<Guid, QueueDwellRecord> QueueDwells { get; }

    /// <summary>
    /// <c>PublishedAt → PreConsumedAt</c> delta for all recorded responses of type
    /// <typeparamref name="TResponse"/>. Only includes entries where both timestamps are
    /// available (i.e. the message implemented <c>IStampable</c> and the filter ran).
    /// </summary>
    IReadOnlyList<TimeSpan> GapsForType<TResponse>() where TResponse : class;

    /// <summary>
    /// Fails the test if any recorded response gap exceeds <paramref name="threshold"/>.
    /// Gaps are only evaluated for pairs where a publish timestamp was captured.
    /// </summary>
    void AssertNoGapsExceeding(TimeSpan threshold);
}

/// <summary>A single PreConsume observation for a response-type message, including the publish timestamp.</summary>
public sealed record ConsumeRecord(Guid RequestId, Type MessageType, DateTimeOffset PublishedAt, DateTimeOffset PreConsumedAt)
{
    /// <summary>End-to-end gap from publish to consume start.</summary>
    public TimeSpan Gap => PreConsumedAt - PublishedAt;
}

/// <summary>A queue-dwell observation for a non-request-response message (e.g. read-side events).</summary>
public sealed record QueueDwellRecord(Guid MessageId, Type MessageType, DateTimeOffset EnqueuedAt, DateTimeOffset PreConsumedAt)
{
    public TimeSpan Dwell => PreConsumedAt - EnqueuedAt;
}
