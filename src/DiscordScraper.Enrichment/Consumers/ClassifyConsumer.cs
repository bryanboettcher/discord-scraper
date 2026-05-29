using System.Diagnostics;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
using DiscordScraper.Enrichment.Ollama;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Classify phase: LLM topic tagging + vector store indexing. Publishes MessageClassified
/// on success so the saga can transition to Enriched.
///
/// Receives the pre-computed embedding from ClassifyMessageRequested (produced by TagConsumer
/// and carried by the saga) so it can upsert the vector point in the same handler.
///
/// No try/catch — exceptions propagate and MT auto-publishes Fault&lt;ClassifyMessageRequested&gt;,
/// which the saga subscribes to for fault handling and replay.
/// </summary>
public sealed class ClassifyConsumer(
    ITaggingClient tagging,
    IVectorStore vectorStore,
    TimeProvider clock,
    ILogger<ClassifyConsumer> logger) : IConsumer<ClassifyMessageRequested>
{
    public async Task Consume(ConsumeContext<ClassifyMessageRequested> context)
    {
        var req = context.Message;
        var sw = Stopwatch.StartNew();

        var text = req.PlainText ?? string.Empty;
        var tagResult = await tagging.TagAsync(text, context.CancellationToken);

        var point = new VectorPoint(
            MessageId: req.MessageSnowflake,
            Embedding: req.Embedding.ToArray().AsMemory(),
            ChannelId: req.ChannelId,
            GuildId: req.GuildId,
            AuthorId: req.AuthorId,
            CreatedAt: req.CreatedAt,
            Tags: tagResult.TopicTags);

        await vectorStore.UpsertManyAsync([point], context.CancellationToken);

        var indexedAt = clock.GetUtcNow();
        sw.Stop();

        logger.LogInformation(
            "Classified {MessageSnowflake} in {ElapsedMs}ms — tags={TagCount} dims={Dims}",
            req.MessageSnowflake, sw.ElapsedMilliseconds, tagResult.TopicTags.Count, req.Embedding.Count);

        await context.Publish<MessageClassified>(new
        {
            req.MessageSnowflake,
            Tags = tagResult.TopicTags,
            ClassifyModelVersion = tagging.Model,
            IndexedAt = indexedAt,
        });
    }
}
