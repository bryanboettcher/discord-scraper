using DiscordScraper.Contracts.Requests;
using MassTransit;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// AnalyzeMessage is a pure-CPU step with no I/O — no concurrency limit needed.
/// Three immediate retries cover transient MT infrastructure faults; the saga's 30s timeout
/// is the upper bound.
/// </summary>
public sealed class AnalyzeMessageConsumerDefinition : ConsumerDefinition<AnalyzeMessageConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<AnalyzeMessageConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(3));
    }
}
