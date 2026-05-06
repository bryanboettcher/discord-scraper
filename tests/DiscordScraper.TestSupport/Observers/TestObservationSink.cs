using System.Collections.Concurrent;

namespace DiscordScraper.TestSupport.Observers;

/// <inheritdoc cref="ITestObservationSink"/>
public sealed class TestObservationSink : ITestObservationSink
{
    private readonly ConcurrentDictionary<Guid, ConsumeRecord> _consumes = new();
    private readonly ConcurrentDictionary<Guid, QueueDwellRecord> _dwells = new();

    // -------------------------------------------------------------------------
    // Write side
    // -------------------------------------------------------------------------

    public void RecordConsume(Guid requestId, Type messageType, DateTimeOffset publishedAt, DateTimeOffset preConsumeAt)
        => _consumes[requestId] = new ConsumeRecord(requestId, messageType, publishedAt, preConsumeAt);

    public void RecordQueueDwell(Guid messageId, Type messageType, DateTimeOffset enqueuedAt, DateTimeOffset preConsumeAt)
        => _dwells[messageId] = new QueueDwellRecord(messageId, messageType, enqueuedAt, preConsumeAt);

    // -------------------------------------------------------------------------
    // Query side
    // -------------------------------------------------------------------------

    public IReadOnlyDictionary<Guid, ConsumeRecord> Consumes => _consumes;
    public IReadOnlyDictionary<Guid, QueueDwellRecord> QueueDwells => _dwells;

    public IReadOnlyList<TimeSpan> GapsForType<TResponse>() where TResponse : class
    {
        var type = typeof(TResponse);
        return _consumes.Values
            .Where(r => r.MessageType == type)
            .Select(r => r.Gap)
            .ToList();
    }

    public void AssertNoGapsExceeding(TimeSpan threshold)
    {
        var violations = _consumes.Values
            .Where(r => r.Gap > threshold)
            .Select(r => $"RequestId={r.RequestId} Type={r.MessageType.Name} Gap={r.Gap.TotalMilliseconds:F1}ms > {threshold.TotalMilliseconds:F1}ms")
            .ToList();

        if (violations.Count > 0)
            throw new AssertionException($"Gap threshold exceeded:\n{string.Join('\n', violations)}");
    }

    /// <summary>Resets all accumulated observations. Useful between test iterations.</summary>
    public void Reset()
    {
        _consumes.Clear();
        _dwells.Clear();
    }
}
