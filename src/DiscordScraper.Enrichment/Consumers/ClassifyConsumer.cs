using System.Diagnostics;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
using DiscordScraper.Enrichment.Ollama;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Enrichment.Consumers;

/// <summary>
/// Classify phase: LLM topic tagging + vector store indexing.
/// Receives the pre-computed embedding from the saga (produced by TagConsumer) so it can
/// upsert the vector point in the same response, removing the separate IndexMessage step.
///
/// No consumer-level retry — rely on saga fault → replay (per spec).
/// </summary>
public sealed class ClassifyConsumer(
    ITaggingClient tagging,
    IVectorStore vectorStore,
    ISystemClock clock,
    ILogger<ClassifyConsumer> logger) : IConsumer<ClassifyMessageRequest>
{
    public async Task Consume(ConsumeContext<ClassifyMessageRequest> context)
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

        var indexedAt = clock.UtcNow;
        sw.Stop();

        logger.LogInformation(
            "Classified {MessageSnowflake} in {ElapsedMs}ms — tags={TagCount} dims={Dims}",
            req.MessageSnowflake, sw.ElapsedMilliseconds, tagResult.TopicTags.Count, req.Embedding.Count);

        await context.RespondAsync(new ClassifyMessageResponse
        {
            Tags = tagResult.TopicTags,
            ClassifyModelVersion = tagging.Model,
            IndexedAt = indexedAt,
        });
    }
}
