using System.Diagnostics;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Enrichment.Ollama;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Tag phase: produces an embedding vector via nomic-embed-text and publishes MessageTagged.
/// Sequential with Classify — the saga publishes ClassifyMessageRequested only after
/// MessageTagged arrives, preventing concurrent GPU load.
///
/// No try/catch — exceptions propagate and MT auto-publishes Fault&lt;TagMessageRequested&gt;,
/// which the saga subscribes to for fault handling and replay.
/// </summary>
public sealed class TagConsumer(
    IEmbeddingClient embedding,
    ILogger<TagConsumer> logger) : IConsumer<TagMessageRequested>
{
    public async Task Consume(ConsumeContext<TagMessageRequested> context)
    {
        var req = context.Message;
        var sw = Stopwatch.StartNew();

        var text = req.PlainText ?? string.Empty;
        var vector = await embedding.EmbedAsync(text, context.CancellationToken);

        sw.Stop();

        logger.LogInformation(
            "Tagged {MessageSnowflake} in {ElapsedMs}ms — dims={Dims}",
            req.MessageSnowflake, sw.ElapsedMilliseconds, vector.Length);

        await context.Publish<MessageTagged>(new
        {
            req.MessageSnowflake,
            Embedding = vector.ToArray(),
            EmbeddingModelVersion = embedding.Model,
        });
    }
}
