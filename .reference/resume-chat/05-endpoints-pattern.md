Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Endpoints/ChatEndpoints.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Endpoints/IngestionEndpoints.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Endpoints/WebApplicationExtensions.cs` (mount call site)

Pattern: one static class per endpoint group in `Endpoints/` with a `public static void MapTo(IEndpointRouteBuilder app)` method. Handlers are private static methods on the same class. `WebApplicationExtensions.MapApplicationEndpoints` is the central mount point, called from `Program.cs`.

## Mount call site (from `WebApplicationExtensions.cs`)

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

And from `Program.cs`:

```csharp
app.MapApplicationEndpoints();
app.MapDefaultEndpoints();
```

## `src/ResumeChat.Api/Endpoints/ChatEndpoints.cs`

```csharp
using System.Runtime.CompilerServices;
using ResumeChat.Api.Extensions;
using ResumeChat.Api.Validation;
using ResumeChat.Rag;
using ResumeChat.Rag.Completion;
using ResumeChat.Rag.Models;
using ResumeChat.Rag.Orchestration;
using ResumeChat.Rag.Response;

namespace ResumeChat.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapTo(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/chat/health", HandleHealth)
            .Produces(StatusCodes.Status200OK);

        app.MapPost("/api/chat", HandleChat)
            .AddEndpointFilter<ValidationFilter<ChatRequest>>()
            .RequireRateLimiting("chat")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);
    }

    private static IResult HandleHealth(IResponseProvider responseProvider)
    {
        var response = new { status = "healthy", provider = (string?)null, model = (string?)null };

        if (responseProvider is ICompletionMetadata meta)
            response = new { status = "healthy", provider = (string?)meta.Provider, model = (string?)meta.Model };

        return Results.Ok(response);
    }

    private static async Task<IResult> HandleChat(
        ChatRequest request,
        IChatOrchestrator orchestrator,
        HttpContext context,
        ILogger<ChatRequest> logger,
        CancellationToken ct)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("chat.request");
        RagDiagnostics.ChatRequests.Add(1);

        activity?.SetTag("chat.message_length", request.Message.Length);
        activity?.SetTag("chat.user_message", request.Message);
        logger.LogInformation("Chat request: {UserMessage}", request.Message);

        try
        {
            var result = await orchestrator.ProcessChatAsync(request, ct);

            context.Response.Headers["X-Threat-Score"] = result.ThreatScore.ToString();

            if (result.CacheHit)
                context.Response.Headers["X-Cache-Hit"] = "true";

            if (result.IsThreat)
            {
                await SingleChunk(ChatResponses.Unrelated, ct)
                    .StreamAsSseAsync(context, cancellationToken: ct);
                return Results.Empty;
            }

            await result.Tokens.StreamAsSseAsync(context, onComplete: fullResponse =>
            {
                activity?.SetTag("chat.response_length", fullResponse.Length);
                activity?.SetTag("chat.response_preview", fullResponse.Length > 500
                    ? fullResponse[..500] + "..."
                    : fullResponse);
                logger.LogInformation("Chat response ({ResponseLength} chars): {ResponsePreview}",
                    fullResponse.Length,
                    fullResponse.Length > 200 ? fullResponse[..200] + "..." : fullResponse);
            }, cancellationToken: ct);

            return Results.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RagDiagnostics.ChatErrors.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static async IAsyncEnumerable<string> SingleChunk(
        string value, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return value;
    }
}
```

## `src/ResumeChat.Api/Endpoints/IngestionEndpoints.cs`

```csharp
using Microsoft.Extensions.Options;
using ResumeChat.Api.Options;
using ResumeChat.Rag.Ingestion;
using ResumeChat.Rag.Pipeline;
using ResumeChat.Rag.VectorStore;

namespace ResumeChat.Api.Endpoints;

public static class IngestionEndpoints
{
    public static void MapTo(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/ingest", HandleIngest)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet("/api/admin/ingest/status", HandleStatus)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> HandleIngest(
        IngestionService ingestion,
        IIngestionPipeline pipeline,
        IOptions<CorpusOptions> corpusOptions,
        HttpContext context,
        ILogger<IngestionService> logger,
        CancellationToken ct)
    {
        var corpusDir = corpusOptions.Value.Directory;
        var isDbBacked = pipeline is not Rag.Ingestion.CorpusIngestionPipeline;
        if (!isDbBacked && !Directory.Exists(corpusDir))
            return Results.Problem("Corpus directory not configured or missing", statusCode: 500);

        context.Response.ContentType = "text/event-stream";

        try
        {
            await foreach (var progress in ingestion.IngestCorpusAsync(corpusDir, ct))
            {
                await context.Response.WriteAsync(
                    $"data: [{progress.ChunksProcessed}] {progress.Status}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }

            await context.Response.WriteAsync("data: [DONE]\n\n", ct);
        }
        catch (OperationCanceledException)
        {
            await context.Response.WriteAsync("data: [CANCELLED]\n\n", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion failed");
            await context.Response.WriteAsync("data: [ERROR] Ingestion failed\n\n", CancellationToken.None);
        }

        await context.Response.Body.FlushAsync(CancellationToken.None);
        return Results.Empty;
    }

    private static async Task<IResult> HandleStatus(
        IVectorStore vectorStore,
        IOptions<CorpusOptions> corpusOptions,
        IOptions<RetrievalOptions> retrievalOptions,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var collection = await vectorStore.GetCollectionInfoAsync(ct);

        return Results.Ok(new
        {
            corpus = new
            {
                directory = corpusOptions.Value.Directory,
                directoryExists = Directory.Exists(corpusOptions.Value.Directory)
            },
            vectorStore = new
            {
                collection = collection.Name,
                pointCount = collection.PointCount,
                vectorDimensions = collection.VectorSize
            },
            pipeline = new
            {
                completionProvider = configuration["Completion:Provider"] ?? "Hardcoded",
                guardProvider = configuration["Guard:Provider"] ?? "Passthrough",
                retrieval = new
                {
                    dimensions = retrievalOptions.Value.Dimensions,
                    topK = retrievalOptions.Value.TopK
                }
            }
        });
    }
}
```
