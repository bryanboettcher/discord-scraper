using DiscordScraper.Api.Endpoints;
using DiscordScraper.Core.Queries;
using DiscordScraper.Read.Configuration;
using DiscordScraper.Read.Extensions;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

builder.Services.AddOptions<PostgresOptions>()
    .BindConfiguration(PostgresOptions.SectionName)
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
