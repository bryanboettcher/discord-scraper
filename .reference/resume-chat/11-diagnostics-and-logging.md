Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/RagDiagnostics.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Api/Extensions/WebApplicationBuilderExtensions.cs` (OTel registration call site)

There is only one diagnostics file — `RagDiagnostics.cs`. It defines a single `ActivitySource` and a single `Meter` plus a handful of counters/histograms/up-down-counters. The default OTel base wiring lives in `ResumeChat.ServiceDefaults.Extensions.ConfigureOpenTelemetry` (see `02-service-defaults.md`); the per-library source/meter names are layered on top in the API host via `ConfigureRagTelemetry` in `WebApplicationBuilderExtensions`.

## `src/ResumeChat.Rag/RagDiagnostics.cs`

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResumeChat.Rag;

public static class RagDiagnostics
{
    public const string ServiceName = "ResumeChat";
    public const string ActivitySourceName = "ResumeChat.Rag";
    public const string MeterName = "ResumeChat.Rag";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    // Counters
    public static readonly Counter<long> ChatRequests =
        Meter.CreateCounter<long>("resumechat.chat.requests", "requests", "Total chat requests");

    public static readonly Counter<long> ChatErrors =
        Meter.CreateCounter<long>("resumechat.chat.errors", "errors", "Chat request errors");

    public static readonly Counter<long> IngestionChunks =
        Meter.CreateCounter<long>("resumechat.ingestion.chunks", "chunks", "Chunks ingested");

    // Histograms
    public static readonly Histogram<double> EmbeddingDuration =
        Meter.CreateHistogram<double>("resumechat.rag.embedding.duration", "ms", "Embedding latency");

    public static readonly Histogram<double> VectorSearchDuration =
        Meter.CreateHistogram<double>("resumechat.rag.vector_search.duration", "ms", "Vector search latency");

    public static readonly Histogram<double> RetrievalDuration =
        Meter.CreateHistogram<double>("resumechat.rag.retrieval.duration", "ms", "Full retrieval latency");

    public static readonly Histogram<double> CompletionFirstTokenDuration =
        Meter.CreateHistogram<double>("resumechat.rag.completion.first_token.duration", "ms", "Completion time to first token");

    public static readonly Histogram<double> CompletionTotalDuration =
        Meter.CreateHistogram<double>("resumechat.rag.completion.total_duration", "ms", "Total completion streaming duration");

    public static readonly Histogram<int> RetrievalResultCount =
        Meter.CreateHistogram<int>("resumechat.rag.retrieval.result_count", "chunks", "Chunks returned per retrieval");

    public static readonly Histogram<double> TopRetrievalScore =
        Meter.CreateHistogram<double>("resumechat.rag.retrieval.top_score", "score", "Highest similarity score per query");

    // Gauges
    public static readonly UpDownCounter<int> IngestionInProgress =
        Meter.CreateUpDownCounter<int>("resumechat.ingestion.in_progress", "operations", "Active ingestion operations");
}
```

## Registration call site — `ConfigureRagTelemetry` in `WebApplicationBuilderExtensions.cs`

```csharp
private static void ConfigureRagTelemetry(this WebApplicationBuilder builder)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddSource(RagDiagnostics.ActivitySourceName))
        .WithMetrics(metrics => metrics.AddMeter(RagDiagnostics.MeterName));
}
```

Called from `AddApplicationServices`:

```csharp
public static WebApplicationBuilder AddApplicationServices(this WebApplicationBuilder builder)
{
    builder.AddServiceDefaults();
    builder.ConfigureRagTelemetry();
    // ...
}
```

## Representative usage from handler / provider code

From `ChatEndpoints.HandleChat` (see `05-endpoints-pattern.md`):

```csharp
using var activity = RagDiagnostics.ActivitySource.StartActivity("chat.request");
RagDiagnostics.ChatRequests.Add(1);

activity?.SetTag("chat.message_length", request.Message.Length);
activity?.SetTag("chat.user_message", request.Message);
// ...
catch (Exception ex) when (ex is not OperationCanceledException)
{
    RagDiagnostics.ChatErrors.Add(1);
    activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
    throw;
}
```

From `OllamaEmbeddingProvider.EmbedAsync` (see `07-ollama-http-client.md`):

```csharp
using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.embed");
activity?.SetTag("rag.embed.model", _options.Model);
activity?.SetTag("rag.embed.text_length", text.Length);

var startTimestamp = Stopwatch.GetTimestamp();
// ... HTTP call ...
var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
activity?.SetTag("rag.embed.dimensions", dimensions);
RagDiagnostics.EmbeddingDuration.Record(elapsedMs);
```

From `QdrantVectorStore.SearchAsync` (see `08-qdrant-vector-store.md`):

```csharp
using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.vector_search");
activity?.SetTag("rag.search.top_k", topK);
// ...
RagDiagnostics.VectorSearchDuration.Record(elapsedMs);
```
