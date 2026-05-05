using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Enrichment.Ollama;
using DiscordScraper.Enrichment.Ollama.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Enrichment.Extensions;

public static class EnrichmentServiceCollectionExtensions
{
    public static IServiceCollection AddEnrichmentClients(this IServiceCollection services)
    {
        // Ollama options carry HTTP identity only (BaseUrl, Model, VectorSize). All middleware
        // and timeout tunables live on EnrichmentTag/EnrichmentClassifyOptions in Contracts —
        // single source of truth so operators don't have to reconcile two config paths.
        services.AddOptions<OllamaTagOptions>()
            .BindConfiguration(OllamaTagOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OllamaClassifyOptions>()
            .BindConfiguration(OllamaClassifyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Tag phase: embedding client (nomic-embed-text). HttpClient timeout sourced from
        // EnrichmentTagOptions.HttpTimeout — must remain shorter than the saga's TagRequest.Timeout
        // (RequestTimeout on EnrichmentTagOptions) so HTTP-layer failures surface to retry/saga
        // before the saga's scheduled timeout fires.
        services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((sp, http) =>
        {
            var ollama = sp.GetRequiredService<IOptions<OllamaTagOptions>>().Value;
            var enrichment = sp.GetRequiredService<IOptions<EnrichmentTagOptions>>().Value;
            http.BaseAddress = new Uri(ollama.BaseUrl);
            http.Timeout = enrichment.HttpTimeout;
        });

        // Classify phase: LLM client (llama3.1:8b). Longer timeout for cold-start; same
        // shorter-than-saga-RequestTimeout invariant.
        services.AddHttpClient<ITaggingClient, OllamaTaggingClient>((sp, http) =>
        {
            var ollama = sp.GetRequiredService<IOptions<OllamaClassifyOptions>>().Value;
            var enrichment = sp.GetRequiredService<IOptions<EnrichmentClassifyOptions>>().Value;
            http.BaseAddress = new Uri(ollama.BaseUrl);
            http.Timeout = enrichment.HttpTimeout;
        });

        return services;
    }
}
