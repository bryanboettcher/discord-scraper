Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Program.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Extensions/ServiceCollectionExtensions.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Endpoints/WebApplicationExtensions.cs`

## `src/ResumeChat.Api/Program.cs`

```csharp
using ResumeChat.Api.Endpoints;
using ResumeChat.Api.Extensions;
using ResumeChat.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.AddApplicationServices();

var app = builder.Build();

app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseStaticFiles();
app.MapApplicationEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program;
```

## `src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs`

```csharp
using FluentValidation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ResumeChat.Api.Options;
using ResumeChat.Rag;
using ResumeChat.Rag.Chunking;
using ResumeChat.Rag.Classification;
using ResumeChat.Rag.Completion;
using ResumeChat.Rag.Embedding;
using ResumeChat.Rag.Ingestion;
using ResumeChat.Rag.Pipeline;
using ResumeChat.Rag.Response;
using ResumeChat.Rag.Retrieval;
using ResumeChat.Rag.VectorStore;
using ResumeChat.Storage.Extensions;
using ResumeChat.Storage.Services;
using ResumeChat.Storage.Repositories;

namespace ResumeChat.Api.Extensions;

public static class WebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddApplicationServices(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.ConfigureRagTelemetry();

        builder.Services.AddOptions<ApiKeyOptions>()
            .BindConfiguration(ApiKeyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddValidatorsFromAssemblyContaining<Program>();
        builder.Services.AddResumeChatRateLimiting(builder.Configuration);
        builder.AddRagServices();

        if (builder.Configuration["Postgres:ConnectionString"] is not null)
            builder.Services.AddResumeChatStorage(builder.Configuration);

        return builder;
    }

    private static void ConfigureRagTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(RagDiagnostics.ActivitySourceName))
            .WithMetrics(metrics => metrics.AddMeter(RagDiagnostics.MeterName));
    }

    private static void AddRagServices(this WebApplicationBuilder builder)
    {
        // Embedding (Ollama)
        builder.Services.AddOptions<OllamaEmbeddingOptions>()
            .BindConfiguration(OllamaEmbeddingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>();

        // Vector store (Qdrant)
        builder.Services.AddOptions<QdrantOptions>()
            .BindConfiguration(QdrantOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>();

        // Corpus config
        builder.Services.AddOptions<Api.Options.CorpusOptions>()
            .BindConfiguration(Api.Options.CorpusOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Chunking
        builder.Services.AddSingleton<IChunkingStrategy, MarkdownSectionChunkingStrategy>();

        // Ingestion — use DB-backed pipeline when Postgres is configured
        if (builder.Configuration["Postgres:ConnectionString"] is not null)
            builder.Services.AddTransient<IIngestionPipeline, DatabaseIngestionPipeline>();
        else
            builder.Services.AddTransient<IIngestionPipeline, CorpusIngestionPipeline>();
        builder.Services.AddTransient<IngestionService>();

        // Retrieval
        builder.Services.AddTransient<IRetrievalProvider, VectorRetrievalProvider>();

        // Threat classification
        var guardProvider = builder.Configuration["Guard:Provider"];
        if (guardProvider == "Ollama")
        {
            builder.Services.AddOptions<OllamaThreatClassifierOptions>()
                .BindConfiguration(OllamaThreatClassifierOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
            builder.Services.AddHttpClient<IThreatClassifier, OllamaThreatClassifier>();
        }
        else
        {
            builder.Services.AddSingleton<IThreatClassifier, PassthroughThreatClassifier>();
        }

        // Security (canary for prompt injection detection)
        builder.Services.AddOptions<CompletionSecurityOptions>()
            .BindConfiguration(CompletionSecurityOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Query pipeline
        builder.Services.AddOptions<RetrievalOptions>()
            .BindConfiguration(RetrievalOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddTransient<IQueryEnricher, SynonymExpansionEnricher>();
        builder.Services.AddTransient<IQueryTransformer, DefaultQueryTransformer>();

        // Response — select provider based on configuration
        var responseProvider = builder.Configuration["Completion:Provider"];
        switch (responseProvider)
        {
            case "Claude":
                builder.Services.AddOptions<ClaudeResponseOptions>()
                    .BindConfiguration(ClaudeResponseOptions.SectionName)
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
                builder.Services.AddHttpClient<IResponseProvider, ClaudeResponseProvider>();
                break;

            case "ClaudeCli":
                builder.Services.AddOptions<ClaudeCliResponseOptions>()
                    .BindConfiguration(ClaudeCliResponseOptions.SectionName)
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
                builder.Services.AddSingleton<IResponseProvider, ClaudeCliResponseProvider>();
                break;

            case "Ollama":
                builder.Services.AddOptions<OllamaResponseOptions>()
                    .BindConfiguration(OllamaResponseOptions.SectionName)
                    .ValidateDataAnnotations()
                    .ValidateOnStart();
                builder.Services.AddHttpClient<IResponseProvider, OllamaResponseProvider>();
                break;

            default:
                builder.Services.AddSingleton<IResponseProvider, CannedResponseProvider>();
                break;
        }
    }
}
```

## `src/ResumeChat.Api/Extensions/ServiceCollectionExtensions.cs`

```csharp
using Microsoft.AspNetCore.RateLimiting;
using ResumeChat.Api.Options;

namespace ResumeChat.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddResumeChatRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>()
                      ?? new RateLimitOptions();

        services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiterOptions.AddFixedWindowLimiter("chat", limiter =>
            {
                limiter.PermitLimit = options.PermitLimit;
                limiter.Window = TimeSpan.FromSeconds(options.WindowSeconds);
                limiter.QueueLimit = 0;
            });
        });

        return services;
    }
}
```

## `src/ResumeChat.Api/Endpoints/WebApplicationExtensions.cs`

```csharp
namespace ResumeChat.Api.Endpoints;

public static class WebApplicationExtensions
{
    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        ChatEndpoints.MapTo(app);
        IngestionEndpoints.MapTo(app);
        CorpusSyncEndpoints.MapTo(app);
        InteractionEndpoints.MapTo(app);

        if (app.Environment.IsDevelopment())
            DebugRetrievalEndpoints.MapTo(app);

        return app;
    }
}
```
