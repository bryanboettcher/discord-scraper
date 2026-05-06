using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Records <c>PreConsume</c> and <c>PostConsume</c> timestamps for a specific response type
/// into <see cref="ITestObservationSink"/>.
///
/// One instance per response type. Register for each of the four enrichment responses
/// (<c>AnalyzeMessageResponse</c>, <c>ProjectMessageResponse</c>, <c>TagMessageResponse</c>,
/// <c>ClassifyMessageResponse</c>) or any future response type via the same generic.
///
/// Gap computation: <c>context.Message.Timestamp</c> (stamped by <see cref="Filters.TimestampFilter{T}"/>
/// at publish time) minus <c>clock.UtcNow</c> at PreConsume is the end-to-end queue dwell.
/// This works across all transports — no send observer required.
///
/// Join key: <c>ConsumeContext.RequestId</c> on the response matches the <c>RequestId</c>
/// set by MT's saga <c>Request()</c> DSL. Available at <c>PreConsume</c> time.
///
/// Why <c>IConsumeMessageObserver{T}</c> over <c>IConsumeObserver</c>: narrower, fires only
/// on the response types we care about. The broad observer sees every message on every
/// consumer and would require type-checking on every PreConsume call.
/// </summary>
public sealed class ResponseConsumeObserver<TResponse>(ITestObservationSink sink, ISystemClock clock)
    : IConsumeMessageObserver<TResponse>
    where TResponse : class
{
    // Stash the PreConsume timestamp per RequestId so PostConsume can read it back.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _preTimestamps = new();

    public Task PreConsume(ConsumeContext<TResponse> context)
    {
        if (context.RequestId is not { } requestId)
            return Task.CompletedTask;

        var preAt = clock.UtcNow;
        _preTimestamps[requestId] = preAt;

        // Derive published-at from the stamped message body when available.
        // Falls back to recording only the consume side (gap unmeasurable) for unstamped responses.
        if (context.Message is IStampable { Timestamp.Ticks: > 0 } stamped)
        {
            var publishedAt = stamped.Timestamp;
            sink.RecordConsume(requestId, typeof(TResponse), publishedAt, preAt);
        }

        return Task.CompletedTask;
    }

    public Task PostConsume(ConsumeContext<TResponse> context)
    {
        // Consume record already written in PreConsume; nothing to do here.
        if (context.RequestId is { } requestId)
            _preTimestamps.TryRemove(requestId, out _);

        return Task.CompletedTask;
    }

    public Task ConsumeFault(ConsumeContext<TResponse> context, Exception exception)
    {
        // Ensure any stashed pre-timestamp is cleaned up on fault path.
        if (context.RequestId is { } requestId)
            _preTimestamps.TryRemove(requestId, out _);

        return Task.CompletedTask;
    }
}
