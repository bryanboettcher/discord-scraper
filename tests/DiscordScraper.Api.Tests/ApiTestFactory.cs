using DiscordScraper.Api.Admin;
using DiscordScraper.Core.Queries;
using Microsoft.Extensions.Time.Testing;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Vector;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Npgsql;
using NSubstitute;

namespace DiscordScraper.Api.Tests;

/// <summary>
/// Spins up the full ASP.NET pipeline with real endpoint routing but mocked query services.
/// The schema-bootstrap hosted services and NpgsqlDataSource are replaced so no Postgres
/// connection is required. ConfigureTestServices runs after Program.cs registrations and wins
/// on duplicate registrations.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public IMessageQueryService MessageQueryService { get; } =
        Substitute.For<IMessageQueryService>();

    public IChannelQueryService ChannelQueryService { get; } =
        Substitute.For<IChannelQueryService>();

    public ISagaIntrospection SagaIntrospection { get; } =
        Substitute.For<ISagaIntrospection>();

    public IReadStoreStatistics ReadStoreStatistics { get; } =
        Substitute.For<IReadStoreStatistics>();

    public IPublishEndpoint PublishEndpoint { get; } =
        Substitute.For<IPublishEndpoint>();

    public FakeTimeProvider SystemClock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Satisfy PostgresOptions, MongoOptions, and RabbitMqOptions ValidateDataAnnotations
        // without a real server.
        builder.UseSetting("Postgres:ConnectionString", "Host=localhost;Database=test;Username=test;Password=test");
        builder.UseSetting("Mongo:ConnectionString", "mongodb://localhost:27017");
        builder.UseSetting("Mongo:DatabaseName", "test");
        builder.UseSetting("RabbitMq:Host", "amqp://localhost");
        builder.UseSetting("RabbitMq:Username", "guest");
        builder.UseSetting("RabbitMq:Password", "guest");

        builder.ConfigureTestServices(services =>
        {
            // Remove hosted services that try to connect to Postgres on startup.
            services.RemoveAll<IHostedService>();

            // Replace the NpgsqlDataSource singleton so Npgsql-dependent singletons don't
            // attempt a real connection if they're resolved during the request pipeline.
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=test;Username=test;Password=test"));

            // Prevent MassTransit from trying to connect to RabbitMQ on startup.
            services.RemoveAll<IBusControl>();
            services.RemoveAll<IBus>();
            services.RemoveAll<IPublishEndpoint>();
            services.RemoveAll<ISendEndpointProvider>();
            services.AddSingleton(PublishEndpoint);
            services.AddSingleton<ISendEndpointProvider>(_ => Substitute.For<ISendEndpointProvider>());
            services.AddSingleton<IBus>(_ => Substitute.For<IBus>());

            // Prevent Mongo client from attempting real connections.
            services.RemoveAll<IMongoClient>();
            services.RemoveAll<IMongoDatabase>();
            services.AddSingleton<IMongoClient>(_ => Substitute.For<IMongoClient>());
            services.AddSingleton<IMongoDatabase>(_ => Substitute.For<IMongoDatabase>());

            // Replace clock with controllable fake.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(SystemClock);

            // Replace the real query services with NSubstitute mocks.
            services.RemoveAll<IMessageQueryService>();
            services.RemoveAll<IChannelQueryService>();
            services.AddSingleton(MessageQueryService);
            services.AddSingleton(ChannelQueryService);

            // Replace admin services with mocks.
            services.RemoveAll<ISagaIntrospection>();
            services.RemoveAll<IReadStoreStatistics>();
            services.AddSingleton(SagaIntrospection);
            services.AddSingleton(ReadStoreStatistics);
        });
    }
}
