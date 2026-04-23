using System.Runtime.CompilerServices;
using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DiscordScraper.Storage.Repositories;

internal sealed class MessageEnrichmentRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IMessageEnrichmentRepository
{
    private const string UpsertSql = """
        INSERT INTO message_enrichments (
            message_id, embedding_model, qdrant_point_id, topic_tags,
            is_substantive, enriched_at, enrichment_version
        ) VALUES (
            @message_id, @embedding_model, @qdrant_point_id, @topic_tags,
            @is_substantive, @enriched_at, @enrichment_version
        )
        ON CONFLICT (message_id) DO UPDATE SET
            embedding_model    = EXCLUDED.embedding_model,
            qdrant_point_id    = EXCLUDED.qdrant_point_id,
            topic_tags         = EXCLUDED.topic_tags,
            is_substantive     = EXCLUDED.is_substantive,
            enriched_at        = EXCLUDED.enriched_at,
            enrichment_version = EXCLUDED.enrichment_version
        """;

    public async IAsyncEnumerable<MessageEntity> EnumerateToEnrichAsync(
        int currentVersion,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (batchSize <= 0) yield break;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Keyset pagination on message_id keeps the query bounded. The
        // anti-join predicate covers both "never enriched" (no row) and "stale
        // enrichment" (row exists but at lower version) in one pass.
        var cursor = 0L;
        while (!ct.IsCancellationRequested)
        {
            var page = await context.Messages
                .AsNoTracking()
                .Where(m => m.MessageId > cursor
                         && !context.MessageEnrichments.Any(e =>
                                e.MessageId == m.MessageId && e.EnrichmentVersion >= currentVersion))
                .OrderBy(m => m.MessageId)
                .Take(batchSize)
                .ToListAsync(ct);

            if (page.Count == 0) yield break;

            foreach (var row in page)
                yield return row;

            cursor = page[^1].MessageId;
            if (page.Count < batchSize) yield break;
        }
    }

    public async Task UpsertAsync(MessageEnrichmentEntity enrichment, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        await context.Database.ExecuteSqlRawAsync(
            UpsertSql,
            parameters: new object[]
            {
                new NpgsqlParameter("message_id", NpgsqlDbType.Bigint) { Value = enrichment.MessageId },
                new NpgsqlParameter("embedding_model", NpgsqlDbType.Text) { Value = enrichment.EmbeddingModel },
                new NpgsqlParameter("qdrant_point_id", NpgsqlDbType.Text) { Value = enrichment.QdrantPointId },
                new NpgsqlParameter("topic_tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = enrichment.TopicTags },
                new NpgsqlParameter("is_substantive", NpgsqlDbType.Boolean) { Value = enrichment.IsSubstantive },
                new NpgsqlParameter("enriched_at", NpgsqlDbType.TimestampTz) { Value = enrichment.EnrichedAt },
                new NpgsqlParameter("enrichment_version", NpgsqlDbType.Integer) { Value = enrichment.EnrichmentVersion },
            },
            cancellationToken: ct);
    }
}
