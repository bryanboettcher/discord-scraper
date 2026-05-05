using DiscordScraper.Contracts.Configuration;
using MassTransit;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Per-endpoint middleware for the Tag (embedding) consumer. Values come from
/// <see cref="EnrichmentTagOptions"/> (Enrichment:Tag) — saga-side and consumer-side
/// tunables for the embedding phase.
///
/// HTTP identity (BaseUrl, Model, HttpClient.Timeout) lives on the separate
/// OllamaTagOptions and is consumed by the Ollama HttpClient registration, not here.
/// </summary>
public sealed class TagConsumerDefinition : ConsumerDefinition<TagConsumer>
{
    private readonly EnrichmentTagOptions _opts;

    public TagConsumerDefinition(IOptions<EnrichmentTagOptions> opts)
    {
        _opts = opts.Value;
        // ConcurrentMessageLimit on the definition is the canonical surface for bounding
        // consumer concurrency; MT applies it via the consumer's pipe filter.
        ConcurrentMessageLimit = _opts.ConcurrentMessageLimit;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TagConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Match prefetch to concurrency or RabbitMQ pushes ahead and the broker buffer
        // thrashes against the consumer semaphore.
        endpointConfigurator.PrefetchCount = _opts.PrefetchCount;

        // Outer-to-inner: KillSwitch sees fault outcomes after retry exhaustion; retry wraps
        // the individual dispatch; concurrency gate is innermost (set on the consumer).
        endpointConfigurator.UseKillSwitch(opts =>
        {
            opts.ActivationThreshold = _opts.KillSwitchActivationThreshold;
            opts.TripThreshold = _opts.KillSwitchTripThreshold;
            opts.TrackingPeriod = _opts.KillSwitchTrackingPeriod;
            opts.RestartTimeout = _opts.KillSwitchRestartTimeout;
        });

        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Exponential(
                _opts.RetryAttempts,
                _opts.RetryMinInterval,
                _opts.RetryMaxInterval,
                _opts.RetryIntervalDelta);
            // Retry transient transport/cancellation; let structured rejections bypass.
            r.Handle<HttpRequestException>();
            r.Handle<TaskCanceledException>();
            r.Ignore<InvalidOperationException>();
        });
    }
}
