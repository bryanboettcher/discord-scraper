using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.Ingestion.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Discord sync worker. Caller is responsible for also
    /// registering <c>AddDiscordScraperStorage()</c>, <c>AddDiscordClient()</c>,
    /// and (on the write host only) <c>AddDiscordScraperMigrations()</c>.
    /// </summary>
    public static IServiceCollection AddDiscordIngestion(this IServiceCollection services)
    {
        services.AddHostedService<DiscordSyncWorker>();
        return services;
    }
}
