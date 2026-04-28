using DiscordScraper.MessageEnhancement.Ollama;
using DiscordScraper.MessageEnhancement.Ollama.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiscordScraper.MessageEnhancement.Extensions;

public static class MessageEnhancementServiceCollectionExtensions
{
    public static IServiceCollection AddMessageEnhancementClients(this IServiceCollection services)
    {
        services.AddOptions<OllamaTaggingOptions>()
            .BindConfiguration(OllamaTaggingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OllamaEmbeddingOptions>()
            .BindConfiguration(OllamaEmbeddingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IOllamaTaggingClient, OllamaTaggingClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaTaggingOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
        });

        services.AddHttpClient<IOllamaEmbeddingClient, OllamaEmbeddingClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaEmbeddingOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
        });

        return services;
    }
}
