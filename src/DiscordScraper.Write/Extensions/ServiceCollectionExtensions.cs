using DiscordScraper.Write.Parsing;
using DiscordScraper.Write.Repositories;
using DiscordScraper.Write.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.Write.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSyncScheduler(this IServiceCollection services)
    {
        services.AddHostedService<SyncSchedulerService>();
        return services;
    }

    /// <summary>
    /// Registers Write-assembly services that are not part of MassTransit registration:
    /// the message parser and the Mongo name-lookup repos used by ProjectMessageConsumer.
    ///
    /// Requires IMongoDatabase to be registered before calling this method (done by the host
    /// project's Mongo setup, same as the saga repo configuration).
    /// </summary>
    public static IServiceCollection AddWriteServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessageParser, MessageParser>();
        services.AddSingleton<IChannelNameRepo, MongoChannelNameRepo>();
        services.AddSingleton<IChannelCursorRepo, MongoChannelCursorRepo>();
        services.AddSingleton<IGuildRoleNameRepo, MongoGuildRoleNameRepo>();
        return services;
    }
}
