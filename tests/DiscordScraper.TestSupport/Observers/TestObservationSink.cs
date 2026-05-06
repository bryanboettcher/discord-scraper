using System.Collections.Concurrent;

namespace DiscordScraper.TestSupport.Observers;

/// <inheritdoc cref="ITestObservationSink"/>
public sealed class TestObservationSink : ITestObservationSink
{
    private readonly ConcurrentDictionary<Guid, SendRecord> _sends = new();
    private readonly ConcurrentDictionary<Guid, ConsumeRecord> _consumes = new();
    private readonly ConcurrentDictionary<Guid, QueueDwellRecord> _dwells = new();

    // -------------------------------------------------------------------------
    // Write side
    // -------------------------------------------------------------------------

    public void RecordSend(Guid requestId, Type messageType, DateTimeOffset timestamp)
        => _sends[requestId] = new SendRecord(requestId, messageType, timestamp);

    public void RecordConsume(Guid requestId, Type messageType, DateTimeOffset preConsumeAt, DateTimeOffset postConsumeAt)
        => _consumes[requestId] = new ConsumeRecord(requestId, messageType, preConsumeAt, postConsumeAt);

    public void RecordQueueDwell(Guid messageId, Type messageType, DateTimeOffset enqueuedAt, DateTimeOffset preConsumeAt)
        => _dwells[messageId] = new QueueDwellRecord(messageId, messageType, enqueuedAt, preConsumeAt);

    // -------------------------------------------------------------------------
    // Query side
    // -------------------------------------------------------------------------

    public IReadOnlyDictionary<Guid, SendRecord> Sends => _sends;
    public IReadOnlyDictionary<Guid, ConsumeRecord> Consumes => _consumes;
    public IReadOnlyDictionary<Guid, QueueDwellRecord> QueueDwells => _dwells;

    public TimeSpan GapForRequest(Guid requestId)
    {
        if (!_sends.TryGetValue(requestId, out var send))
            throw new InvalidOperationException($"No send record for RequestId={requestId}");
        if (!_consumes.TryGetValue(requestId, out var consume))
            throw new InvalidOperationException($"No consume record for RequestId={requestId}");
        return consume.PreConsumedAt - send.PostSentAt;
    }

    public IReadOnlyList<TimeSpan> GapsForType<TRequest>() where TRequest : class
    {
        var type = typeof(TRequest);
        var gaps = new List<TimeSpan>();
        foreach (var (id, send) in _sends)
        {
            if (send.MessageType != type) continue;
            if (!_consumes.TryGetValue(id, out var consume)) continue;
            gaps.Add(consume.PreConsumedAt - send.PostSentAt);
        }
        return gaps;
    }

    public void AssertNoGapsExceeding(TimeSpan threshold)
    {
        var violations = new List<string>();
        foreach (var (id, send) in _sends)
        {
            if (!_consumes.TryGetValue(id, out var consume)) continue;
            var gap = consume.PreConsumedAt - send.PostSentAt;
            if (gap > threshold)
                violations.Add($"RequestId={id} Type={send.MessageType.Name} Gap={gap.TotalMilliseconds:F1}ms > {threshold.TotalMilliseconds:F1}ms");
        }
        if (violations.Count > 0)
            throw new AssertionException($"Gap threshold exceeded:\n{string.Join('\n', violations)}");
    }

    public void AssertAllConsumedWithin(TimeSpan threshold)
    {
        var violations = new List<string>();
        foreach (var (id, send) in _sends)
        {
            if (!_consumes.TryGetValue(id, out var consume))
            {
                violations.Add($"RequestId={id} Type={send.MessageType.Name} was sent but never consumed");
                continue;
            }
            var gap = consume.PreConsumedAt - send.PostSentAt;
            if (gap > threshold)
                violations.Add($"RequestId={id} Type={send.MessageType.Name} Gap={gap.TotalMilliseconds:F1}ms > {threshold.TotalMilliseconds:F1}ms");
        }
        if (violations.Count > 0)
            throw new AssertionException($"Not all requests consumed within threshold:\n{string.Join('\n', violations)}");
    }

    /// <summary>Resets all accumulated observations. Useful between test iterations.</summary>
    public void Reset()
    {
        _sends.Clear();
        _consumes.Clear();
        _dwells.Clear();
    }
}
