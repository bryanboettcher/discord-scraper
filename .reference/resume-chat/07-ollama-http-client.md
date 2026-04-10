Sources:
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/Embedding/OllamaEmbeddingProvider.cs`
- `/home/insta/src/bryanboettcher/resume/backend/src/ResumeChat.Rag/Response/OllamaResponseProvider.cs`

Both are `AddHttpClient<TInterface, TImpl>()` typed HTTP clients. `OllamaEmbeddingProvider` posts to `/api/embed`; `OllamaResponseProvider` streams from `/api/chat` line-by-line (NDJSON), yielding tokens as `IAsyncEnumerable<string>`. Both private-nest their JSON DTOs with `JsonPropertyName` attributes.

## `src/ResumeChat.Rag/Embedding/OllamaEmbeddingProvider.cs`

```csharp
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ResumeChat.Rag.Embedding;

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly OllamaEmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingProvider> _logger;

    public OllamaEmbeddingProvider(
        HttpClient httpClient,
        IOptions<OllamaEmbeddingOptions> options,
        ILogger<OllamaEmbeddingProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.embed");
        activity?.SetTag("rag.embed.model", _options.Model);
        activity?.SetTag("rag.embed.text_length", text.Length);

        var startTimestamp = Stopwatch.GetTimestamp();

        var request = new OllamaEmbedRequest(_options.Model, text);
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.BaseUrl.TrimEnd('/')}/api/embed",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(cancellationToken);

        if (result?.Embeddings is not { Count: > 0 })
            throw new InvalidOperationException("Ollama returned no embeddings.");

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var dimensions = result.Embeddings[0].Length;

        activity?.SetTag("rag.embed.dimensions", dimensions);
        RagDiagnostics.EmbeddingDuration.Record(elapsedMs);

        _logger.LogDebug("Embedded {TextLength} chars with {Model} → {Dimensions}d in {ElapsedMs:F1}ms",
            text.Length, _options.Model, dimensions, elapsedMs);

        return result.Embeddings[0];
    }

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]> Embeddings);
}
```

## `src/ResumeChat.Rag/Response/OllamaResponseProvider.cs`

```csharp
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResumeChat.Rag.Completion;
using ResumeChat.Rag.Models;

namespace ResumeChat.Rag.Response;

public sealed class OllamaResponseProvider : ResponseProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly OllamaResponseOptions _options;
    private readonly CompletionSecurityOptions _security;

    public OllamaResponseProvider(
        HttpClient httpClient,
        IOptions<OllamaResponseOptions> options,
        IOptions<CompletionSecurityOptions> security,
        ILogger<OllamaResponseProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _security = security.Value;
    }

    protected override string ProviderName => "Ollama";
    protected override string ModelName => _options.Model;

    protected override async IAsyncEnumerable<string> StreamTokensAsync(
        QueryPayload payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var systemPrompt = SystemPromptBuilder.Build(payload, _security.Canary);

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        if (payload.History is { Count: > 0 })
        {
            foreach (var exchange in payload.History)
            {
                messages.Add(new { role = "user", content = exchange.Prompt });
                messages.Add(new { role = "assistant", content = exchange.Response });
            }
        }
        messages.Add(new { role = "user", content = payload.OriginalMessage });

        var body = new
        {
            model = _options.Model,
            messages,
            stream = true
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/api/chat")
        {
            Content = JsonContent.Create(body)
        };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
            if (chunk?.Message?.Content is { Length: > 0 } content)
                yield return content;

            if (chunk?.Done == true)
                yield break;
        }
    }

    private sealed record OllamaChatChunk(
        [property: JsonPropertyName("message")] OllamaChatMessage? Message,
        [property: JsonPropertyName("done")] bool Done);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("content")] string Content);
}
```
