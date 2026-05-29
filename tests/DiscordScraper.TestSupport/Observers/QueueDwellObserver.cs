using DiscordScraper.Contracts;
using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// General <see cref="IConsumeObserver"/> for measuring queue-dwell time on non-request-response
/// messages, primarily the read-side <c>BatchProjectionPipeline</c> consumers.
///
/// Join key: <c>MessageId</c> (not <c>RequestId</c>) because these messages are not part
/// of a request-response pair.
///
/// Why <c>PostConsume</c> (not <c>PreConsume</c>): MT's pipeline executes
/// <c>IConsumeObserver.PreConsume</c> BEFORE <c>UseConsumeFilter</c> filters. The
/// <see cref="Filters.InboundTimestampFilter{T}"/> stamps <c>ReceivedOn</c> during the filter
/// pass, after <c>PreConsume</c> but before <c>PostConsume</c>. Reading <c>IMeasured.ReceivedOn</c>
/// in <c>PostConsume</c> ensures the stamp is populated.
///
/// Enqueued-at priority:
/// 1. <c>context.Message is IMeasured { Timestamp.Ticks: &gt; 0 }</c> — stamped by
///    <see cref="Filters.OutboundTimestampFilter{T}"/> at publish time. <c>ReceivedOn</c>
///    is also available from the same message when ticks &gt; 0.
/// 2. <c>context.Message is IStampable { Timestamp.Ticks: &gt; 0 }</c> — stamped at publish
///    time but no receive-side stamp; fall back to clock.GetUtcNow() for the receive timestamp.
/// 3. <c>context.SentTime</c> — broker-reported accept time. Non-null on RabbitMQ; null on
///    InMemory transport.
/// 4. <c>DateTimeOffset.MinValue</c> — sentinel when no enqueued-at signal is available
///    (InMemory + non-IStampable). The record still appears in
///    <see cref="ITestObservationSink.QueueDwells"/> so callers can notice rather than miss it.
///    Filter by <c>EnqueuedAt != DateTimeOffset.MinValue</c> to exclude these from dwell analysis.
/// </summary>
public sealed class QueueDwellObserver(ITestObservationSink sink, TimeProvider clock)
    : IConsumeObserver
{
    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
        => Task.CompletedTask;

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        var messageId = context.MessageId ?? Guid.NewGuid();

        DateTimeOffset enqueued;
        DateTimeOffset preConsumeAt;

        if (context.Message is IMeasured { Timestamp.Ticks: > 0 } measured)
        {
            enqueued = measured.Timestamp;
            preConsumeAt = measured.ReceivedOn.Ticks > 0 ? measured.ReceivedOn : clock.GetUtcNow();
        }
        else if (context.Message is IStampable { Timestamp.Ticks: > 0 } stampable)
        {
            enqueued = stampable.Timestamp;
            preConsumeAt = clock.GetUtcNow();
        }
        else if (context.SentTime.HasValue)
        {
            enqueued = new DateTimeOffset(context.SentTime.Value, TimeSpan.Zero);
            preConsumeAt = clock.GetUtcNow();
        }
        else
        {
            enqueued = DateTimeOffset.MinValue;
            preConsumeAt = clock.GetUtcNow();
        }

        sink.RecordQueueDwell(messageId, typeof(T), enqueued, preConsumeAt);
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        => Task.CompletedTask;
}
