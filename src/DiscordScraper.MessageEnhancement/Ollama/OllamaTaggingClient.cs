using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordScraper.MessageEnhancement.Ollama.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.MessageEnhancement.Ollama;

internal sealed class OllamaTaggingClient(
    HttpClient http,
    IOptions<OllamaTaggingOptions> options,
    ILogger<OllamaTaggingClient> logger) : IOllamaTaggingClient
{
    // Constrain the model to a narrow, chat-flavored classification task. The
    // server runs this with format=json so the response body is always valid
    // JSON; failed JSON shape means a model bug, not a malformed prompt.
    private const string Instructions = """
        You classify short chat messages from a homelab and software-development Discord server.
        Return a JSON object with exactly these fields:
          - topic_tags: array of 1 to 3 short lowercase single-word keywords naming the main topic (examples: ["docker","postgres","debugging"]). Prefer concrete technical terms. No punctuation, no hashtags.
          - is_substantive: boolean. true if the message communicates a question, answer, explanation, decision, or technical observation. false for greetings, reactions, single-word acknowledgements, pure emoji, or bare URLs with no commentary.
        """;

    private readonly OllamaTaggingOptions _options = options.Value;

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
            throw new InvalidOperationException("Ollama tagging returned empty response");

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
            // Local LLMs occasionally return malformed JSON even with format=json.
            // Log the offender, treat as non-substantive with no tags so the
            // enrichment row still lands and we don't re-process forever.
            logger.LogWarning(ex,
                "Tagger returned unparseable JSON for {Chars}-char message; defaulting to non-substantive. Payload: {Payload}",
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
