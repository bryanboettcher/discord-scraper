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
/// Join key: <c>ConsumeContext.RequestId</c> on the response matches the <c>RequestId</c>
/// recorded by <see cref="RequestSendObserver"/> at send time. MT sets this before
/// <c>IConsumeMessageObserver.PreConsume</c> fires.
///
/// Why <c>IConsumeMessageObserver{T}</c> over <c>IConsumeObserver</c>: narrower, fires only
/// on the response types we care about. The broad observer sees every message on every
/// consumer and would require type-checking on every PreConsume call.
/// </summary>
public sealed class ResponseConsumeObserver<TResponse>(ITestObservationSink sink, TimeProvider? timeProvider = null)
    : IConsumeMessageObserver<TResponse>
    where TResponse : class
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    // Stash the PreConsume timestamp per RequestId so PostConsume can read it back.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _preTimestamps = new();

    public Task PreConsume(ConsumeContext<TResponse> context)
    {
        if (context.RequestId is { } requestId)
            _preTimestamps[requestId] = _time.GetUtcNow();

        return Task.CompletedTask;
    }

    public Task PostConsume(ConsumeContext<TResponse> context)
    {
        if (context.RequestId is { } requestId
            && _preTimestamps.TryRemove(requestId, out var preAt))
        {
            sink.RecordConsume(requestId, typeof(TResponse), preAt, _time.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    public Task ConsumeFault(ConsumeContext<TResponse> context, Exception exception)
    {
        // Still record what we have so failed responses don't silently disappear from analysis.
        if (context.RequestId is { } requestId
            && _preTimestamps.TryRemove(requestId, out var preAt))
        {
            sink.RecordConsume(requestId, typeof(TResponse), preAt, _time.GetUtcNow());
        }

        return Task.CompletedTask;
    }
}
