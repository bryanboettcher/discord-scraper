using DiscordScraper.Contracts.Configuration;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Per-endpoint middleware for the Classify (LLM) consumer. Values come from
/// <see cref="EnrichmentClassifyOptions"/> (Enrichment:Classify).
///
/// No consumer-level retry — the saga's Faulted state plus replay-via-CorrelateBy is the
/// canonical recovery path. A single transient blip should not multiply LLM-cost retries
/// while the GPU slot is held; let the saga see the fault and replay later when the
/// admin endpoint or a scheduled fan-out fires.
/// </summary>
public sealed class ClassifyConsumerDefinition : ConsumerDefinition<ClassifyConsumer>
{
    private readonly EnrichmentClassifyOptions _opts;

    public ClassifyConsumerDefinition(IOptions<EnrichmentClassifyOptions> opts)
    {
        _opts = opts.Value;
        // ConcurrentMessageLimit = 1 is the structural fix for GPU thrash — only one LLM
        // inference can occupy the shared Vulkan card at a time across the whole ingester.
        ConcurrentMessageLimit = _opts.ConcurrentMessageLimit;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ClassifyConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.PrefetchCount = _opts.PrefetchCount;

        endpointConfigurator.UseKillSwitch(opts =>
        {
            opts.ActivationThreshold = _opts.KillSwitchActivationThreshold;
            opts.TripThreshold = _opts.KillSwitchTripThreshold;
            opts.TrackingPeriod = _opts.KillSwitchTrackingPeriod;
            opts.RestartTimeout = _opts.KillSwitchRestartTimeout;
        });
    }
}
