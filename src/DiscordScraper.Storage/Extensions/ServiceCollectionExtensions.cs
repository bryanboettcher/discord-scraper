using DiscordScraper.Storage.Options;
using DiscordScraper.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="DiscordScraperDbContext"/>, repositories, and
    /// options for any host that needs database access. Does NOT register
    /// <see cref="MigrationHostedService"/> — only the ingester should apply
    /// migrations; call <see cref="AddDiscordScraperMigrations"/> separately
    /// from that host.
    /// </summary>
    public static IServiceCollection AddDiscordScraperStorage(this IServiceCollection services)
    {
        services.AddOptions<PostgresOptions>()
            .BindConfiguration(PostgresOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddPooledDbContextFactory<DiscordScraperDbContext>((sp, builder) =>
        {
            var postgres = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            builder.UseNpgsql(postgres.ConnectionString);
        });

        services.AddTransient<IRawMessageRepository, RawMessageRepository>();
        services.AddTransient<IRawGuildRepository, RawGuildRepository>();
        services.AddTransient<IRawChannelRepository, RawChannelRepository>();
        services.AddTransient<ISyncStateRepository, SyncStateRepository>();
        services.AddTransient<IIngestionRunRepository, IngestionRunRepository>();
        services.AddTransient<IMessageRepository, MessageRepository>();
        services.AddTransient<IMessageEnrichmentRepository, MessageEnrichmentRepository>();
        services.AddTransient<IRawPinRepository, RawPinRepository>();
        services.AddTransient<IRawMessageEditRepository, RawMessageEditRepository>();

        return services;
    }

    /// <summary>
    /// Registers the startup-time migration runner. Call this exactly once,
    /// from the host that is responsible for schema evolution (the ingester).
    /// </summary>
    public static IServiceCollection AddDiscordScraperMigrations(this IServiceCollection services)
    {
        services.AddHostedService<MigrationHostedService>();
        return services;
    }
}
