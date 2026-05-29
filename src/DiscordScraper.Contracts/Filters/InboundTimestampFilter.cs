using MassTransit;

namespace DiscordScraper.Contracts.Filters;

/// <summary>
/// Consume-pipe filter that stamps the receive-side timestamp into any <see cref="IMeasured"/>
/// message whose <c>ReceivedOn</c> is still at the zero default.
///
/// Registration: <c>cfg.UseConsumeFilter(typeof(InboundTimestampFilter&lt;&gt;), context)</c>
/// on the bus factory configurator.
///
/// Why <c>where T : class</c> (not <c>where T : class, IMeasured</c>): MT's open-generic filter
/// registration closes the generic against every message type seen on the consume pipe. A constraint
/// tighter than <c>class</c> would throw <c>ArgumentException</c> ("GenericArguments[0] violates
/// the constraint") for non-IMeasured message types. The runtime <c>is IMeasured</c> check is the
/// safe pattern.
///
/// Pipeline ordering: <c>IConsumeObserver.PreConsume</c> fires BEFORE <c>UseConsumeFilter</c>
/// filters. This filter stamps <c>ReceivedOn</c> before <c>PostConsume</c>, so observers that
/// read <c>ReceivedOn</c> must do so in <c>PostConsume</c>.
/// </summary>
public sealed class InboundTimestampFilter<T>(TimeProvider clock) : IFilter<ConsumeContext<T>>
    where T : class
{
    public Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (context.Message is IMeasured { ReceivedOn.Ticks: 0 } measured)
            measured.ReceivedOn = clock.GetUtcNow();
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("inbound-timestamp");
}
