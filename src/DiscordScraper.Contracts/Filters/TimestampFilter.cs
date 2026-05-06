using DiscordScraper.Contracts.Clock;
using MassTransit;

namespace DiscordScraper.Contracts.Filters;

/// <summary>
/// Send/publish pipeline filter that stamps a publish-time timestamp into the body of any
/// <see cref="IStampable"/> message whose <c>Timestamp</c> is still at the zero default.
///
/// Registration: call both
/// <c>cfg.UseSendFilter(typeof(TimestampFilter&lt;&gt;), context)</c> and
/// <c>cfg.UsePublishFilter(typeof(TimestampFilter&lt;&gt;), context)</c> on the bus factory
/// configurator. Publish does not flow through the send pipe by default; <c>RespondAsync</c>
/// is covered for free via the send pipe.
///
/// Why both pipes: MT routes Request() saga sends through the send pipe, while IBus.Publish
/// goes through the publish pipe. Registering on both ensures all outbound message paths are
/// stamped regardless of origin.
///
/// Why direct assignment instead of <c>CreateProxy</c>: the InMemory transport's Send() method
/// casts the context back to its own concrete <c>InMemorySendContext&lt;T&gt;</c> type. A proxy
/// returned by <c>CreateProxy</c> is a different object and would fail this cast. Direct mutation
/// on <c>context.Message</c> (possible because <c>Timestamp</c> is <c>{ get; set; }</c>) happens
/// before the body's lazy serialization is evaluated, so the stamped value is included in the
/// wire bytes on all transports.
/// </summary>
public sealed class TimestampFilter<T>(ISystemClock clock) : IFilter<SendContext<T>>
    where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        if (context.Message is IStampable { Timestamp.Ticks: 0 } stampable)
            stampable.Timestamp = clock.UtcNow;

        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("timestamp");
}
