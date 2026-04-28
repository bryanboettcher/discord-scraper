using System.Text.Json;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.MessageEnhancement.Analysis;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.MessageEnhancement.Consumers;

/// <summary>
/// Fast inline analysis: substantiveness heuristic, bot detection, language detection.
/// No I/O — this consumer completes in microseconds and does not need batching or
/// a concurrency limit beyond the endpoint default.
///
/// Language detection decision: hardcoded "en" for v1. The owner's target guild is
/// English-speaking; non-English support is a Stage 2 concern. Adding NTextCat or
/// Lingua is out of scope for an inline synchronous step. A CJK fast-path heuristic
/// (count high-codepoint characters) was considered but rejected: the value is minimal
/// given the corpus, and the classification boundary is ambiguous for mixed-language
/// messages. Revisit when multi-language corpus exists.
/// </summary>
public sealed class AnalyzeMessageConsumer(ILogger<AnalyzeMessageConsumer> logger) : IConsumer<AnalyzeMessageRequest>
{
    public Task Consume(ConsumeContext<AnalyzeMessageRequest> context)
    {
        var msg = context.Message;

        var (content, isBot) = ParsePayload(msg, logger);

        // AuthorIsBot on the request is the canonical source — the saga stamps it from
        // MessageCaptured which in turn reads author.bot from the Discord payload.
        // We also parse from PayloadJson as a cross-check / fallback for callers that
        // don't populate AuthorIsBot (e.g., tests driving the consumer directly).
        var botResult = msg.AuthorIsBot || isBot;

        var trivial = SubstantivenessFilter.IsObviouslyTrivial(content);
        var substantive = !trivial && !botResult;

        logger.LogInformation(
            "AnalyzeMessage message_id={MessageSnowflake} is_substantive={IsSubstantive} is_bot={IsBot} lang={Lang}",
            msg.MessageSnowflake, substantive, botResult, "en");

        return context.RespondAsync(new AnalyzeMessageResponse
        {
            IsSubstantive = substantive,
            IsBot = botResult,
            DetectedLanguage = "en",
        });
    }

    private static (string Content, bool IsBot) ParsePayload(
        AnalyzeMessageRequest msg,
        ILogger<AnalyzeMessageConsumer> log)
    {
        if (string.IsNullOrWhiteSpace(msg.PayloadJson))
            return (string.Empty, false);

        try
        {
            using var doc = JsonDocument.Parse(msg.PayloadJson);
            var root = doc.RootElement;

            var content = root.TryGetProperty("content", out var contentProp)
                ? contentProp.GetString() ?? string.Empty
                : string.Empty;

            var isBot = false;
            if (root.TryGetProperty("author", out var author) &&
                author.TryGetProperty("bot", out var botProp))
            {
                isBot = botProp.ValueKind == JsonValueKind.True;
            }

            return (content, isBot);
        }
        catch (JsonException ex)
        {
            // Malformed payload: treat as non-substantive and let the saga exclude it.
            // The saga's Faulted path handles harder failures; JSON parse errors are data
            // quality issues that should not crash the consumer or burn a retry.
            log.LogWarning(ex,
                "AnalyzeMessage message_id={MessageSnowflake} PayloadJson parse failed; treating as non-substantive",
                msg.MessageSnowflake);

            return (string.Empty, false);
        }
    }
}
