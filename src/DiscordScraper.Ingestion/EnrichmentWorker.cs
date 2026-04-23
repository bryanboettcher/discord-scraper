using DiscordScraper.Ingestion.Enrichment;
using DiscordScraper.Ingestion.Ollama;
using DiscordScraper.Ingestion.Options;
using DiscordScraper.Ingestion.Qdrant;
using DiscordScraper.Storage.Entities;
using DiscordScraper.Storage.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Ingestion;

/// <summary>
/// Third of three independent workers. Walks <c>messages</c> whose
/// <c>message_enrichments</c> row is missing or at a lower
/// <see cref="EnrichmentOptions.Version"/>, runs them through Ollama (tagging
/// + embedding for substantive content), upserts the embedded vector into
/// Qdrant, then writes the enrichment bookkeeping row.
/// </summary>
public sealed class EnrichmentWorker(
    IMessageEnrichmentRepository enrichmentRepo,
    IOllamaEmbeddingClient embedding,
    IOllamaTaggingClient tagging,
    IQdrantVectorStore qdrant,
    IIngestionRunRepository runRepo,
    IOptions<EnrichmentOptions> enrichmentOptions,
    IOptions<OllamaEmbeddingOptions> embeddingOptions,
    ILogger<EnrichmentWorker> logger) : BackgroundService
{
    private const string WorkerName = "enrichment";

    private readonly EnrichmentOptions _options = enrichmentOptions.Value;
    private readonly int _vectorSize = embeddingOptions.Value.VectorSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Enrichment worker starting; version={Version}, batch={Batch}, interval={Interval}, tagging={Tagging}, embedding={Embedding}",
            _options.Version, _options.BatchSize, _options.PollInterval,
            tagging.Model, embedding.Model);

        var collectionReady = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!collectionReady)
            {
                try
                {
                    await qdrant.EnsureCollectionAsync(_vectorSize, stoppingToken);
                    collectionReady = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Qdrant collection bootstrap failed, retrying in 10s");
                    try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    continue;
                }
            }

            var runId = await runRepo.StartAsync(WorkerName, stoppingToken);
            var processed = 0;

            try
            {
                processed = await RunPassAsync(stoppingToken);
                await runRepo.MarkSuccessAsync(runId, processed, stoppingToken);
                if (processed > 0)
                    logger.LogInformation("Enrichment pass: {N} messages processed", processed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await runRepo.MarkFailedAsync(
                    runId, processed, "cancelled during shutdown", CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Enrichment pass crashed");
                await runRepo.MarkFailedAsync(runId, processed, ex.Message, CancellationToken.None);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        var processed = 0;
        await foreach (var message in enrichmentRepo.EnumerateToEnrichAsync(_options.Version, _options.BatchSize, ct))
        {
            try
            {
                await EnrichOneAsync(message, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-message isolation. A persistent failure on one message
                // would otherwise block the whole backlog forever; by logging
                // and moving on, operators see the error in OTEL + runs log
                // while the rest of the corpus keeps enriching. Next pass
                // retries because no enrichment row was written.
                logger.LogError(ex,
                    "Enrichment failed for message {MessageId}; skipping, will retry next pass",
                    message.MessageId);
            }
        }
        return processed;
    }

    private async Task EnrichOneAsync(MessageEntity message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (SubstantivenessFilter.IsObviouslyTrivial(message.Content))
        {
            // Shortcut: no LLM call, no Qdrant point, but we still write the
            // enrichment row so the next pass doesn't re-visit this message.
            await enrichmentRepo.UpsertAsync(new MessageEnrichmentEntity
            {
                MessageId = message.MessageId,
                EmbeddingModel = embedding.Model,
                QdrantPointId = "",
                TopicTags = [],
                IsSubstantive = false,
                EnrichedAt = now,
                EnrichmentVersion = _options.Version,
            }, ct);
            return;
        }

        var taggingInput = message.Content.Length > _options.MaxTaggingContentChars
            ? message.Content[.._options.MaxTaggingContentChars]
            : message.Content;
        var tag = await tagging.TagAsync(taggingInput, ct);

        var pointId = string.Empty;
        if (tag.IsSubstantive)
        {
            var vector = await embedding.EmbedAsync(message.Content, ct);
            pointId = QdrantVectorStore.GeneratePointId(message.MessageId, embedding.Model);

            await qdrant.UpsertAsync(new MessageVectorRecord(
                PointId: pointId,
                Vector: vector,
                MessageId: message.MessageId,
                ChannelId: message.ChannelId,
                GuildId: message.GuildId,
                RootChannelId: message.RootChannelId,
                ThreadId: message.ThreadId,
                AuthorName: message.AuthorName,
                CreatedAt: message.CreatedAt,
                TopicTags: tag.TopicTags,
                Content: message.Content
            ), ct);
        }

        await enrichmentRepo.UpsertAsync(new MessageEnrichmentEntity
        {
            MessageId = message.MessageId,
            EmbeddingModel = embedding.Model,
            QdrantPointId = pointId,
            TopicTags = tag.TopicTags.ToArray(),
            IsSubstantive = tag.IsSubstantive,
            EnrichedAt = now,
            EnrichmentVersion = _options.Version,
        }, ct);
    }
}
