using DiscordScraper.Core.Graph;
using DiscordScraper.Core.Queries;
using DiscordScraper.Core.Search;
using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Configuration;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Graph;
using DiscordScraper.Read.Queries;
using DiscordScraper.Read.Search;
using DiscordScraper.Read.Vector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

namespace DiscordScraper.Read.Extensions;

public static class ReadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pgvector-backed <see cref="IVectorStore"/> and the schema bootstrap hosted
    /// service. Requires <see cref="PostgresOptions"/> to be bound before this call. Must be
    /// invoked before <see cref="AddReadModels"/> and <see cref="AddReadGraph"/> because it
    /// registers the shared <see cref="NpgsqlDataSource"/> singleton with vector type mapping.
    /// </summary>
    public static IServiceCollection AddReadVectorStore(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            var builder = new NpgsqlDataSourceBuilder(opts.ConnectionString);
            builder.UseVector();
            return builder.Build();
        });

        services.AddSingleton<IVectorStore, PgVectorStore>();
        services.AddHostedService<MessageVectorsSchemaInitializer>();

        return services;
    }

    /// <summary>
    /// Registers the pooled <see cref="ReadDbContext"/> factory, the read-side schema bootstrap,
    /// and <see cref="ISearchService"/>. Reuses the <see cref="NpgsqlDataSource"/> registered by
    /// <see cref="AddReadVectorStore"/> so EF inherits vector type mapping.
    /// </summary>
    public static IServiceCollection AddReadModels(this IServiceCollection services)
    {
        services.AddPooledDbContextFactory<ReadDbContext>((sp, optsBuilder) =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
            optsBuilder.UseNpgsql(dataSource);
        });

        services.AddHostedService<ReadSchemaInitializer>();
        services.AddSingleton<ISearchService, PgSearchService>();

        return services;
    }

    /// <summary>
    /// Registers the recursive-CTE-backed <see cref="IConversationGraph"/>. Depends on the
    /// <see cref="NpgsqlDataSource"/> registered by <see cref="AddReadVectorStore"/>.
    /// </summary>
    public static IServiceCollection AddReadGraph(this IServiceCollection services)
    {
        services.AddSingleton<IConversationGraph, PgConversationGraph>();
        return services;
    }

    /// <summary>
    /// Registers query services (<see cref="IMessageQueryService"/>, <see cref="IChannelQueryService"/>)
    /// that compose the storage abstractions for the MCP front-end. Requires
    /// <see cref="AddReadVectorStore"/>, <see cref="AddReadModels"/>, and <see cref="AddReadGraph"/>
    /// to have been called.
    /// </summary>
    public static IServiceCollection AddReadQueries(this IServiceCollection services)
    {
        services.AddSingleton<IMessageQueryService, MessageQueryService>();
        services.AddSingleton<IChannelQueryService, ChannelQueryService>();
        return services;
    }
}
