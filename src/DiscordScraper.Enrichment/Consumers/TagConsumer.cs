using System.Diagnostics;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Enrichment.Ollama;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Tag phase: produces an embedding vector via nomic-embed-text.
/// Sequential with Classify — the saga fires ClassifyRequest only after this completes,
/// preventing concurrent GPU load that caused 286 timeouts with the old parallel Task.WhenAll.
/// </summary>
public sealed class TagConsumer(
    IEmbeddingClient embedding,
    ILogger<TagConsumer> logger) : IConsumer<TagMessageRequest>
{
    public async Task Consume(ConsumeContext<TagMessageRequest> context)
    {
        var req = context.Message;
        var sw = Stopwatch.StartNew();

        var text = req.PlainText ?? string.Empty;
        var vector = await embedding.EmbedAsync(text, context.CancellationToken);

        sw.Stop();

        logger.LogInformation(
            "Tagged {MessageSnowflake} in {ElapsedMs}ms — dims={Dims}",
            req.MessageSnowflake, sw.ElapsedMilliseconds, vector.Length);

        await context.RespondAsync(new TagMessageResponse
        {
            Embedding = vector.ToArray(),
            EmbeddingModelVersion = embedding.Model,
            GeneratedAt = DateTimeOffset.UtcNow,
        });
    }
}
