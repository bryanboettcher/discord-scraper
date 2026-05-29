using DiscordScraper.Api.Admin;
using DiscordScraper.Api.Endpoints;
using DiscordScraper.Core.Configuration;
using DiscordScraper.Core.Queries;
using DiscordScraper.Read.Configuration;
using DiscordScraper.Read.Extensions;
using MassTransit;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

builder.Services.AddOptions<PostgresOptions>()
    .BindConfiguration(PostgresOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MongoOptions>()
    .BindConfiguration(MongoOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration(RabbitMqOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Ordering matters: VectorStore creates the shared NpgsqlDataSource singleton; Models and Graph
// depend on it. Queries depend on all three.
builder.Services.AddReadVectorStore();
builder.Services.AddReadModels();
builder.Services.AddReadGraph();
builder.Services.AddReadQueries();

builder.Services.AddOptions<MessageQueryServiceOptions>()
    .BindConfiguration(MessageQueryServiceOptions.SectionName);

// Admin data plane — Mongo client for saga collection reads.
// No MongoBsonRegistration: admin reads via BsonDocument projections only.
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    return new MongoClient(opts.ConnectionString);
});

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>()
      .GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.DatabaseName));

// Publish-only MassTransit — no consumers, no sagas, no outbox.
// Admin commands publish directly to RabbitMQ; the Ingester picks them up.
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var opts = ctx.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        cfg.Host(new Uri(opts.Host), h =>
        {
            h.Username(opts.Username);
            h.Password(opts.Password);
        });
        // Matches Ingester topology so MT's exchange/queue declarations stay consistent.
        cfg.UseDelayedMessageScheduler();
    });
});

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAdminServices();

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

app.MapApplicationEndpoints();
app.MapDefaultEndpoints();
app.MapOpenApi();

// Interactive API explorer — dev only; MCP sibling uses /openapi/v1.json directly.
if (app.Environment.IsDevelopment())
    app.MapScalarApiReference();

app.Run();

// Exposes Program as a partial class so WebApplicationFactory<Program> can resolve it
// from the test assembly without InternalsVisibleTo gymnastics.
public partial class Program;
