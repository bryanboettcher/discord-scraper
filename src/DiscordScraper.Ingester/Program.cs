using DiscordScraper.Core.Configuration;
using DiscordScraper.Discord.Extensions;
using DiscordScraper.Ingester.SmokeTest;
using DiscordScraper.MessageEnhancement;
using DiscordScraper.MessageEnhancement.Extensions;
using DiscordScraper.Read;
using DiscordScraper.Read.Extensions;
using DiscordScraper.Read.Configuration;
using DiscordScraper.Write;
using DiscordScraper.Write.Configuration;
using DiscordScraper.Write.Extensions;
using DiscordScraper.Write.Sagas;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Extensions.DiagnosticSources;

// Register BSON class maps for the polymorphic MessageNode hierarchy before the Mongo
// driver is used. Must run before any saga with a MessageIR field is read or written.
MongoBsonRegistration.RegisterAll();

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// --- Options ---
builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration(RabbitMqOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MongoOptions>()
    .BindConfiguration(MongoOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PostgresOptions>()
    .BindConfiguration(PostgresOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// --- MongoDB client + database ---
// MongoClientSettings configures the DiagnosticSources activity propagation
// so MongoDB operations appear in the OTel trace.
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<MongoOptions>>().Value;
    var settings = MongoClientSettings.FromConnectionString(opts.ConnectionString);
    settings.ClusterConfigurator = cb => cb.Subscribe(new DiagnosticsActivityEventSubscriber());
    return new MongoClient(settings);
});

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>()
      .GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.DatabaseName));

// --- Discord REST client + Write/Enhancement services ---
builder.Services.AddDiscordClient();
builder.Services.AddWriteServices();
builder.Services.AddMessageEnhancementClients();

// PgVectorStore + dedicated NpgsqlDataSource with vector type mapping. Must register before
// AddReadModels so the EF context factory inherits the same data source.
builder.Services.AddReadVectorStore();

// --- MassTransit ---
var mongoOptions = builder.Configuration.GetSection(MongoOptions.SectionName).Get<MongoOptions>()
    ?? throw new InvalidOperationException("Mongo configuration section is missing.");

builder.Services.AddMassTransit(x =>
{
    x.AddWriteSagasAndConsumers(mongoOptions.ConnectionString, mongoOptions.DatabaseName);
    x.AddConsumers(typeof(Program).Assembly);
    x.AddConsumers(typeof(MessageEnhancementAssemblyMarker).Assembly);
    x.AddConsumers(typeof(ReadAssemblyMarker).Assembly);

    // Outbox requires Mongo running as a single-node replica set (docker-compose configures rs0).
    x.AddMongoDbOutbox(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
        o.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        var opts = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        cfg.Host(new Uri(opts.Host), h =>
        {
            h.Username(opts.Username);
            h.Password(opts.Password);
        });

        cfg.UseDelayedMessageScheduler();
        cfg.ConfigureEndpoints(context);
    });
});

// --- Read-side wire-up ---
// AddReadModels registers ReadSchemaInitializer (creates tables, hypertable, continuous aggregate
// on first boot) and PgSearchService; both share the NpgsqlDataSource registered above.
builder.Services.AddReadModels();
builder.Services.AddReadGraph();
builder.Services.AddReadQueries();

// Periodically publishes GuildSyncRequested for each configured Discord guild.
builder.Services.AddSyncScheduler();

// Boot-time smoke test: proves bus, RabbitMQ, Mongo outbox, and a consumer all round-trip.
builder.Services.AddSingleton<BusSmokeTestCompletionSource>();
builder.Services.AddHostedService<BusSmokeTestService>();

var host = builder.Build();
host.Run();
