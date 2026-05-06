using MassTransit;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// General <see cref="IConsumeObserver"/> for measuring queue-dwell time on non-request-response
/// messages, primarily the read-side <c>BatchProjectionPipeline</c> consumers.
///
/// Join key: <c>MessageId</c> (not <c>RequestId</c>) because these messages are not part
/// of a request-response pair. <c>SentTime</c> on the <c>ReceiveContext</c> reflects when the
/// broker accepted the message (the enqueue timestamp); <c>PreConsume</c> fires when MT begins
/// dispatching to the consumer. The delta is the broker queue-dwell time.
///
/// If <c>SentTime</c> is null (some transports don't populate it), the record is still written
/// with <c>EnqueuedAt = DateTimeOffset.MinValue</c> so it appears in <see cref="ITestObservationSink.QueueDwells"/>
/// and can be noticed rather than silently dropped.
/// </summary>
public sealed class QueueDwellObserver(ITestObservationSink sink, TimeProvider? timeProvider = null)
    : IConsumeObserver
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        var messageId = context.MessageId ?? Guid.NewGuid();
        var enqueued = context.SentTime.HasValue
            ? new DateTimeOffset(context.SentTime.Value, TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        sink.RecordQueueDwell(messageId, typeof(T), enqueued, _time.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
        => Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        => Task.CompletedTask;
}
