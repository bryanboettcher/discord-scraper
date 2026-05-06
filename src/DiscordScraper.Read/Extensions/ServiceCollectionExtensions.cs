using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Core.Graph;
using DiscordScraper.Core.Queries;
using DiscordScraper.Core.Search;
using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Configuration;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Graph;
using DiscordScraper.Read.Mapping;
using DiscordScraper.Read.Queries;
using DiscordScraper.Read.Search;
using DiscordScraper.Read.Vector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>().Value;
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
    /// <see cref="ISearchService"/>, the open-generic <see cref="IBulkWriter{TEntity}"/>, and all
    /// per-table projectors. Reuses the <see cref="NpgsqlDataSource"/> registered by
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

        // Open-generic IBulkWriter<TEntity> — resolves EfCoreBulkWriter<T> for any read entity.
        services.AddScoped(typeof(IBulkWriter<>), typeof(EfCoreBulkWriter<>));

        // Open-generic BatchProjectionPipeline<TEvent,TEntity> — DI closes per request,
        // injected into each thin concrete read-side consumer.
        services.AddScoped(typeof(BatchProjectionPipeline<,>));

        // Per-table projectors — stateless, singleton.
        services.AddSingleton<IBatchProjector<ChannelChanged, ReadChannel>, MapperlyChannelChangedProjector>();
        services.AddSingleton<IBatchProjector<GuildChanged, ReadGuild>, MapperlyGuildChangedProjector>();
        services.AddSingleton<IBatchProjector<MessageEnriched, ReadMessage>, MapperlyReadMessageProjector>();
        services.AddSingleton<IBatchProjector<MessageEnriched, MessageReference>, MapperlyMessageReferenceProjector>();
        services.AddSingleton<IBatchProjector<MessageEnriched, MessageAttachment>, MapperlyMessageAttachmentProjector>();
        services.AddSingleton<IBatchProjector<MessageEnriched, MessageEmbed>, MapperlyMessageEmbedProjector>();
        services.AddSingleton<IBatchProjector<MessageEnriched, MessageTag>, MapperlyMessageTagProjector>();

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
