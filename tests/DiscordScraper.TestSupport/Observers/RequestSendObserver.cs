using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Records <c>PostSend</c> timestamps for request-type messages into <see cref="ITestObservationSink"/>.
///
/// Registration: connect via <c>IBus.ConnectSendObserver(observer)</c> or via
/// <see cref="ObservationServiceCollectionExtensions"/>. One instance covers all message
/// types because <c>ISendObserver</c> is not generic.
///
/// Join key: <c>SendContext.RequestId</c> is set before <c>PostSend</c> fires and matches
/// the <c>RequestId</c> on the response's <c>ConsumeContext</c>. This is the unambiguous
/// one-to-one link documented in the masstransit-expert findings.
/// </summary>
public sealed class RequestSendObserver(ITestObservationSink sink, TimeProvider? timeProvider = null)
    : ISendObserver
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task PreSend<T>(SendContext<T> context) where T : class
        => Task.CompletedTask;

    public Task PostSend<T>(SendContext<T> context) where T : class
    {
        // RequestId is set on both the outgoing request AND the response reply.
        // Discriminate by ResponseAddress: a request has a ResponseAddress (the reply queue);
        // a response send does not (it's already responding to the original request).
        // Only record the outgoing request, not the response reply.
        if (context.RequestId is { } requestId && context.ResponseAddress is not null)
            sink.RecordSend(requestId, typeof(T), _time.GetUtcNow());

        return Task.CompletedTask;
    }

    public Task SendFault<T>(SendContext<T> context, Exception exception) where T : class
        => Task.CompletedTask;
}
