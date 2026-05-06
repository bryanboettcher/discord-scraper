using DiscordScraper.Contracts;
using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Records transport measurements for a specific response type into <see cref="ITestObservationSink"/>.
///
/// One instance per response type. Register for each of the four enrichment responses
/// (<c>AnalyzeMessageResponse</c>, <c>ProjectMessageResponse</c>, <c>TagMessageResponse</c>,
/// <c>ClassifyMessageResponse</c>) or any future response type via the same generic.
///
/// Why <c>PostConsume</c> (not <c>PreConsume</c>): MT's pipeline executes
/// <c>IConsumeObserver.PreConsume</c> BEFORE <c>UseConsumeFilter</c> filters. The
/// <see cref="Filters.InboundTimestampFilter{T}"/> stamps <c>ReceivedOn</c> during the filter
/// pass, which happens after <c>PreConsume</c> but before <c>PostConsume</c>. Reading both
/// <c>Timestamp</c> and <c>ReceivedOn</c> in <c>PostConsume</c> guarantees both are populated.
///
/// Join key: <c>ConsumeContext.RequestId</c> on the response matches the <c>RequestId</c>
/// set by MT's saga <c>Request()</c> DSL. Available at <c>PostConsume</c> time.
///
/// Why <c>IConsumeMessageObserver{T}</c> over <c>IConsumeObserver</c>: narrower, fires only
/// on the response types we care about. The broad observer sees every message on every
/// consumer and would require type-checking on every call.
/// </summary>
public sealed class ResponseConsumeObserver<TResponse>(ITestObservationSink sink)
    : IConsumeMessageObserver<TResponse>
    where TResponse : class
{
    public Task PreConsume(ConsumeContext<TResponse> context) => Task.CompletedTask;

    public Task PostConsume(ConsumeContext<TResponse> context)
    {
        if (context.RequestId is not { } requestId)
            return Task.CompletedTask;

        if (context.Message is IMeasured { Timestamp.Ticks: > 0, ReceivedOn.Ticks: > 0 } measured)
            sink.RecordConsume(requestId, typeof(TResponse), measured.Timestamp, measured.ReceivedOn);

        return Task.CompletedTask;
    }

    public Task ConsumeFault(ConsumeContext<TResponse> context, Exception exception) => Task.CompletedTask;
}
