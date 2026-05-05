using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DiscordScraper.Enrichment.Ollama.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Enrichment.Ollama;

internal sealed class OllamaEmbeddingClient(
    HttpClient http,
    IOptions<OllamaTagOptions> options,
    ILogger<OllamaEmbeddingClient> logger) : IEmbeddingClient
{
    private readonly OllamaTagOptions _options = options.Value;

    public string Model => _options.Model;

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbedRequest(_options.Model, text);
        var response = await http.PostAsJsonAsync(
            $"{_options.BaseUrl.TrimEnd('/')}/api/embed", request, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        if (result?.Embeddings is not { Count: > 0 } || result.Embeddings[0] is null)
            throw new InvalidOperationException("Ollama returned no embedding vector");

        var vector = result.Embeddings[0];
        logger.LogDebug(
            "Embedded {Chars} chars with {Model} → {Dims}d",
            text.Length, _options.Model, vector.Length);

        return vector;
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]> Embeddings);
}
