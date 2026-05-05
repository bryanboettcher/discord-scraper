namespace DiscordScraper.Contracts.Configuration;

/// <summary>
/// Consumer middleware + saga-side tuning for the Tag phase (embedding via nomic-embed-text).
/// Binds to Enrichment:Tag. Separate from OllamaTagOptions (Ollama:Tag) which configures the
/// HTTP client identity; these configure MT consumer middleware and saga request timeouts.
/// </summary>
public sealed class EnrichmentTagOptions
{
    public const string SectionName = "Enrichment:Tag";

    // Consumer concurrency / prefetch
    public int ConcurrentMessageLimit { get; init; } = 8;
    public int PrefetchCount { get; init; } = 8;

    // HttpClient timeout (must be shorter than the saga RequestTimeout envelope)
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // Consumer-level retry (exponential)
    public int RetryAttempts { get; init; } = 2;
    public TimeSpan RetryMinInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan RetryMaxInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryIntervalDelta { get; init; } = TimeSpan.FromMilliseconds(500);

    // Saga-side Request<>.Timeout; must exceed the consumer's HttpTimeout + retry envelope.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // KillSwitch
    public int KillSwitchActivationThreshold { get; init; } = 10;
    public int KillSwitchTripThreshold { get; init; } = 20;        // percent
    public TimeSpan KillSwitchTrackingPeriod { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan KillSwitchRestartTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Consumer middleware + saga-side tuning for the Classify phase (LLM via llama3.1:8b).
/// Binds to Enrichment:Classify. No consumer-level retry — rely on saga fault → replay.
/// </summary>
public sealed class EnrichmentClassifyOptions
{
    public const string SectionName = "Enrichment:Classify";

    // ConcurrentMessageLimit = 1: each LLM call occupies the GPU for the full duration.
    public int ConcurrentMessageLimit { get; init; } = 1;
    public int PrefetchCount { get; init; } = 1;

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(90);

    // Saga-side timeout must exceed the full LLM cold-start envelope.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(180);

    // KillSwitch
    public int KillSwitchActivationThreshold { get; init; } = 5;
    public int KillSwitchTripThreshold { get; init; } = 20;        // percent
    public TimeSpan KillSwitchTrackingPeriod { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan KillSwitchRestartTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
