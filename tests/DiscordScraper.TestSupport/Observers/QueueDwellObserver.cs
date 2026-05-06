using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// General <see cref="IConsumeObserver"/> for measuring queue-dwell time on non-request-response
/// messages, primarily the read-side <c>BatchProjectionPipeline</c> consumers.
///
/// Join key: <c>MessageId</c> (not <c>RequestId</c>) because these messages are not part
/// of a request-response pair.
///
/// Enqueued-at priority:
/// 1. <c>context.Message is IStampable { Timestamp.Ticks: &gt; 0 }</c> — stamped by
///    <see cref="Filters.TimestampFilter{T}"/> at publish time. Works across all transports.
/// 2. <c>context.SentTime</c> — broker-reported accept time. Non-null on RabbitMQ; null on
///    InMemory transport.
/// 3. <c>DateTimeOffset.MinValue</c> — sentinel when no enqueued-at signal is available
///    (InMemory + non-IStampable). The record still appears in
///    <see cref="ITestObservationSink.QueueDwells"/> so callers can notice rather than miss it.
///    Filter by <c>EnqueuedAt != DateTimeOffset.MinValue</c> to exclude these from dwell analysis.
/// </summary>
public sealed class QueueDwellObserver(ITestObservationSink sink, ISystemClock clock)
    : IConsumeObserver
{
    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        var messageId = context.MessageId ?? Guid.NewGuid();
        var preConsumeAt = clock.UtcNow;

        DateTimeOffset enqueued;
        if (context.Message is IStampable { Timestamp.Ticks: > 0 } stampable)
        {
            enqueued = stampable.Timestamp;
        }
        else if (context.SentTime.HasValue)
        {
            enqueued = new DateTimeOffset(context.SentTime.Value, TimeSpan.Zero);
        }
        else
        {
            enqueued = DateTimeOffset.MinValue;
        }

        sink.RecordQueueDwell(messageId, typeof(T), enqueued, preConsumeAt);
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
        => Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        => Task.CompletedTask;
}
