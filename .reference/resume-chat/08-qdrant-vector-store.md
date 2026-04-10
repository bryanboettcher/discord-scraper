Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/VectorStore/QdrantVectorStore.cs`

`EnsureCollectionAsync` is the collection-bootstrap method; there is no separate bootstrap class. It probes `GET /collections/{name}` and issues `PUT` to create with cosine distance if missing. Point IDs are deterministic UUIDs from SHA256 of `source_file:section_heading:chunk_index`.

## `src/ResumeChat.Rag/VectorStore/QdrantVectorStore.cs`

```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResumeChat.Rag.Models;

namespace ResumeChat.Rag.VectorStore;

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _httpClient;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(
        HttpClient httpClient,
        IOptions<QdrantOptions> options,
        ILogger<QdrantVectorStore> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync(int vectorSize, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/collections/{_options.CollectionName}";

        var check = await _httpClient.GetAsync(url, cancellationToken);
        if (check.IsSuccessStatusCode)
        {
            _logger.LogDebug("Collection {Collection} already exists", _options.CollectionName);
            return;
        }

        _logger.LogInformation("Creating collection {Collection} with vector size {VectorSize}",
            _options.CollectionName, vectorSize);

        var body = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = "Cosine"
            }
        };

        var response = await _httpClient.PutAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpsertAsync(EmbeddedChunk chunk, CancellationToken cancellationToken = default)
    {
        var pointId = GeneratePointId(chunk.Chunk);

        var point = new
        {
            points = new[]
            {
                new
                {
                    id = pointId,
                    vector = chunk.Embedding.ToArray(),
                    payload = new Dictionary<string, object>
                    {
                        ["text"] = chunk.Chunk.Text,
                        ["section_heading"] = chunk.Chunk.SectionHeading,
                        ["chunk_index"] = chunk.Chunk.ChunkIndex,
                        ["source_file"] = chunk.Chunk.Metadata.SourceFile,
                        ["title"] = chunk.Chunk.Metadata.Title ?? "",
                        ["tags"] = chunk.Chunk.Metadata.Tags
                    }
                }
            }
        };

        var url = $"{BaseUrl}/collections/{_options.CollectionName}/points";
        var response = await _httpClient.PutAsJsonAsync(url, point, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        int? dimensions = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.vector_search");
        activity?.SetTag("rag.search.top_k", topK);

        var startTimestamp = Stopwatch.GetTimestamp();

        // When truncating, fetch more candidates from full-dim search, then re-rank
        var fetchLimit = dimensions.HasValue ? Math.Max(topK * 4, 50) : topK;
        var needVectors = dimensions.HasValue;

        var body = new
        {
            vector = queryEmbedding.ToArray(),
            limit = fetchLimit,
            with_payload = true,
            with_vectors = needVectors
        };

        var url = $"{BaseUrl}/collections/{_options.CollectionName}/points/search";
        var response = await _httpClient.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(cancellationToken);

        if (result?.Result is null)
        {
            _logger.LogWarning("Qdrant search returned null result for top_k={TopK}", topK);
            RagDiagnostics.VectorSearchDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
            return [];
        }

        IReadOnlyList<ScoredChunk> chunks;

        if (dimensions.HasValue && dimensions.Value < queryEmbedding.Length)
        {
            var dims = dimensions.Value;
            var truncQuery = TruncateAndNormalize(queryEmbedding.Span, dims);

            activity?.SetTag("rag.search.truncated_dimensions", dims);
            activity?.SetTag("rag.search.fetch_limit", fetchLimit);

            chunks = result.Result
                .Where(r => r.Vector is not null)
                .Select(r =>
                {
                    var truncStored = TruncateAndNormalize(r.Vector.AsSpan(), dims);
                    var cosine = DotProduct(truncQuery, truncStored);
                    return (Result: r, Score: cosine);
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => ToScoredChunk(x.Result, x.Score))
                .ToList();
        }
        else
        {
            chunks = result.Result
                .Take(topK)
                .Select(r => ToScoredChunk(r, r.Score))
                .ToList();
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        activity?.SetTag("rag.search.result_count", chunks.Count);
        activity?.SetTag("rag.search.top_score", chunks.Count > 0 ? chunks[0].Score : 0);
        RagDiagnostics.VectorSearchDuration.Record(elapsedMs);

        _logger.LogDebug("Qdrant search returned {ResultCount} results in {ElapsedMs:F1}ms (top score: {TopScore:F4})",
            chunks.Count, elapsedMs, chunks.Count > 0 ? chunks[0].Score : 0);

        return chunks;
    }

    private static ScoredChunk ToScoredChunk(QdrantSearchResult r, float score)
    {
        var payload = r.Payload;
        var metadata = new DocumentMetadata(
            GetString(payload, "source_file"),
            GetStringOrNull(payload, "title"),
            payload.TryGetValue("tags", out var tagsEl) ? DeserializeTags(tagsEl) : []);

        var chunk = new DocumentChunk(
            GetString(payload, "text"),
            GetString(payload, "section_heading"),
            payload.TryGetValue("chunk_index", out var idx) && idx.TryGetInt32(out var i) ? i : 0,
            metadata);

        return new ScoredChunk(chunk, score);
    }

    private static float[] TruncateAndNormalize(ReadOnlySpan<float> embedding, int dims)
    {
        var truncated = new float[dims];
        embedding[..dims].CopyTo(truncated);

        var sumSq = 0f;
        for (var j = 0; j < truncated.Length; j++)
            sumSq += truncated[j] * truncated[j];

        var norm = MathF.Sqrt(sumSq);
        if (norm > 0)
        {
            for (var j = 0; j < truncated.Length; j++)
                truncated[j] /= norm;
        }

        return truncated;
    }

    // Both inputs are L2-normalized by TruncateAndNormalize, so cosine similarity = dot product
    private static float DotProduct(float[] a, float[] b)
    {
        var dot = 0f;
        for (var j = 0; j < a.Length; j++)
            dot += a[j] * b[j];
        return dot;
    }

    public async Task<CollectionInfo> GetCollectionInfoAsync(CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/collections/{_options.CollectionName}";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new CollectionInfo(_options.CollectionName, 0, 0);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var result = json.GetProperty("result");

        var pointCount = result.GetProperty("points_count").GetInt64();
        var vectorSize = result.GetProperty("config").GetProperty("params").GetProperty("vectors").GetProperty("size").GetInt32();

        return new CollectionInfo(_options.CollectionName, pointCount, vectorSize);
    }

    private string BaseUrl => _options.BaseUrl.TrimEnd('/');

    private static string GeneratePointId(DocumentChunk chunk)
    {
        var input = $"{chunk.Metadata.SourceFile}:{chunk.SectionHeading}:{chunk.ChunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Qdrant accepts UUID-format string IDs — use first 16 bytes as a deterministic UUID
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static string GetString(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var el) ? el.GetString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var el) ? el.GetString() : null;

    private static IReadOnlyList<string> DeserializeTags(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        return element.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList();
    }

    private sealed record QdrantSearchResponse(
        [property: JsonPropertyName("result")] IReadOnlyList<QdrantSearchResult> Result);

    private sealed record QdrantSearchResult(
        [property: JsonPropertyName("score")] float Score,
        [property: JsonPropertyName("payload")] Dictionary<string, JsonElement> Payload,
        [property: JsonPropertyName("vector")] float[]? Vector = null);
}
```
