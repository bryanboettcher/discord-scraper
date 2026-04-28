using DiscordScraper.Core.Queries;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Satisfy PostgresOptions ValidateDataAnnotations without a real server.
        builder.UseSetting("Postgres:ConnectionString", "Host=localhost;Database=test;Username=test;Password=test");

        builder.ConfigureTestServices(services =>
        {
            // Remove hosted services that try to connect to Postgres on startup.
            services.RemoveAll<IHostedService>();

            // Replace the NpgsqlDataSource singleton so Npgsql-dependent singletons don't
            // attempt a real connection if they're resolved during the request pipeline.
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=test;Username=test;Password=test"));

            // Replace the real query services with NSubstitute mocks.
            services.RemoveAll<IMessageQueryService>();
            services.RemoveAll<IChannelQueryService>();
            services.AddSingleton(MessageQueryService);
            services.AddSingleton(ChannelQueryService);
        });
    }
}
