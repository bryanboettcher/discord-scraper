using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordScraper.Enrichment.Ollama.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Enrichment.Ollama;

internal sealed class OllamaTaggingClient(
    HttpClient http,
    IOptions<OllamaClassifyOptions> options,
    ILogger<OllamaTaggingClient> logger) : ITaggingClient
{
    private const string Instructions = """
        You classify short chat messages from a homelab and software-development Discord server.
        Return a JSON object with exactly these fields:
          - topic_tags: array of 1 to 3 short lowercase single-word keywords naming the main topic (examples: ["docker","postgres","debugging"]). Prefer concrete technical terms. No punctuation, no hashtags.
          - is_substantive: boolean. true if the message communicates a question, answer, explanation, decision, or technical observation. false for greetings, reactions, single-word acknowledgements, pure emoji, or bare URLs with no commentary.
        """;

    private readonly OllamaClassifyOptions _options = options.Value;

    public string Model => _options.Model;

    public async Task<TagResult> TagAsync(string content, CancellationToken ct = default)
    {
        var prompt = $"{Instructions}\n\nMessage:\n{content}";
        var request = new GenerateRequest(_options.Model, prompt, Stream: false, Format: "json");

        var response = await http.PostAsJsonAsync(
            $"{_options.BaseUrl.TrimEnd('/')}/api/generate", request, ct);

        response.EnsureSuccessStatusCode();

        var parsed = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct);
        if (string.IsNullOrWhiteSpace(parsed?.Response))
            throw new InvalidOperationException("Ollama classify returned empty response");

        return ParseTagResult(parsed.Response, content, logger);
    }

    private static TagResult ParseTagResult(string responseJson, string originalContent, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            var tags = new List<string>();
            if (root.TryGetProperty("topic_tags", out var tagArr) && tagArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagArr.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString()?.Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(s)) tags.Add(s);
                    }
                }
            }

            var substantive = root.TryGetProperty("is_substantive", out var subEl)
                && subEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                && subEl.GetBoolean();

            return new TagResult(tags, substantive);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Classifier returned unparseable JSON for {Chars}-char message; defaulting to non-substantive. Payload: {Payload}",
                originalContent.Length,
                responseJson.Length > 500 ? responseJson[..500] + "…" : responseJson);
            return new TagResult([], IsSubstantive: false);
        }
    }

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("done")] bool Done);
}
