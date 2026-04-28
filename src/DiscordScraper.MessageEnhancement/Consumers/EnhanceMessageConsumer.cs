using System.Diagnostics;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.MessageEnhancement.Ollama;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.MessageEnhancement.Consumers;

/// <summary>
/// Runs tagging and embedding in parallel and returns a single combined response. Issuing one
/// request/response from the saga (instead of two) avoids fan-in OCC collisions when both calls
/// would otherwise update the same saga document concurrently.
/// </summary>
public sealed class EnhanceMessageConsumer(
    IOllamaTaggingClient tagging,
    IOllamaEmbeddingClient embedding,
    ILogger<EnhanceMessageConsumer> logger) : IConsumer<EnhanceMessageRequest>
{
    public async Task Consume(ConsumeContext<EnhanceMessageRequest> context)
    {
        var req = context.Message;
        var sw = Stopwatch.StartNew();

        // Flat plain text is the right input for both the classifier and the sentence transformer.
        // An empty string (e.g., attachment-only message) is acceptable — tagging returns no tags
        // and embedding returns a model-default vector; the saga still reaches Enriched.
        var text = req.PlainText ?? string.Empty;

        var tagsTask = tagging.TagAsync(text, context.CancellationToken);
        var embedTask = embedding.EmbedAsync(text, context.CancellationToken);

        var tagResult = await tagsTask;
        var vector = await embedTask;

        sw.Stop();

        logger.LogInformation(
            "Enhanced {MessageSnowflake} in {ElapsedMs}ms — tags={TagCount} dims={Dims}",
            req.MessageSnowflake, sw.ElapsedMilliseconds, tagResult.TopicTags.Count, vector.Length);

        await context.RespondAsync(new EnhanceMessageResponse(
            Tags: tagResult.TopicTags,
            Embedding: vector.ToArray(),
            EmbeddingModelVersion: embedding.Model,
            TagModelVersion: tagging.Model));
    }
}
