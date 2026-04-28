using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.Api.Admin;

public static class AdminServiceExtensions
{
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddSingleton<ISagaIntrospection, MongoSagaIntrospection>();
        services.AddSingleton<IReadStoreStatistics, PgReadStoreStatistics>();
        return services;
    }
}
