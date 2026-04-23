using DiscordScraper.Ingestion.Ollama;
using DiscordScraper.Ingestion.Options;
using DiscordScraper.Ingestion.Qdrant;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.Ingestion.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all three ingestion-side BackgroundServices (sync → projection
    /// → enrichment) plus the Ollama and Qdrant typed HttpClients the
    /// enrichment worker depends on. The workers share no in-process state
    /// and coordinate only via Postgres + Qdrant. Caller is responsible for
    /// also registering <c>AddDiscordScraperStorage()</c>,
    /// <c>AddDiscordClient()</c>, and (on the write host only)
    /// <c>AddDiscordScraperMigrations()</c>.
    /// </summary>
    public static IServiceCollection AddDiscordIngestion(this IServiceCollection services)
    {
        services.AddOptions<ProjectionOptions>()
            .BindConfiguration(ProjectionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EnrichmentOptions>()
            .BindConfiguration(EnrichmentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OllamaEmbeddingOptions>()
            .BindConfiguration(OllamaEmbeddingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OllamaTaggingOptions>()
            .BindConfiguration(OllamaTaggingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<QdrantOptions>()
            .BindConfiguration(QdrantOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IOllamaEmbeddingClient, OllamaEmbeddingClient>();
        services.AddHttpClient<IOllamaTaggingClient, OllamaTaggingClient>();
        services.AddHttpClient<IQdrantVectorStore, QdrantVectorStore>();

        services.AddHostedService<DiscordSyncWorker>();
        services.AddHostedService<ProjectionWorker>();
        services.AddHostedService<EnrichmentWorker>();
        return services;
    }
}
