using DiscordScraper.Core.Vector;
using Microsoft.Extensions.Logging;
using Npgsql;
using PgVector = Pgvector.Vector;

namespace DiscordScraper.Read.Vector;

/// <summary>
/// pgvector implementation of IVectorStore using raw Npgsql — no EF Core.
/// EF's vector(N) mapping is awkward and adds overhead on a path that is already
/// doing expensive Ollama round-trips; raw Npgsql keeps batch upserts direct and
/// lets NpgsqlBatch amortize round-trips across a full consumer batch.
///
/// Embedding dimension is hardcoded at 768 to match nomic-embed-text (Ollama default).
/// Bumping to a different model dimension requires dropping and recreating message_vectors;
/// the HNSW index is dimension-specific and cannot be migrated in place.
/// </summary>
internal sealed class PgVectorStore(NpgsqlDataSource dataSource, ILogger<PgVectorStore> logger) : IVectorStore
{
    private const int EmbeddingDimensions = 768;

    private const string UpsertSql = """
        INSERT INTO message_vectors
          (message_id, embedding, channel_id, guild_id, author_id, created_at, tags)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        ON CONFLICT (message_id) DO UPDATE SET
          embedding   = EXCLUDED.embedding,
          channel_id  = EXCLUDED.channel_id,
          guild_id    = EXCLUDED.guild_id,
          author_id   = EXCLUDED.author_id,
          created_at  = EXCLUDED.created_at,
          tags        = EXCLUDED.tags;
        """;

    public async Task UpsertManyAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0)
        {
            logger.LogDebug("UpsertManyAsync called with empty list; skipping");
            return;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn);

        foreach (var point in points)
        {
            var cmd = new NpgsqlBatchCommand(UpsertSql);
            cmd.Parameters.Add(new NpgsqlParameter<long>  { Value = point.MessageId });
            cmd.Parameters.Add(new NpgsqlParameter<PgVector>{ Value = new PgVector(point.Embedding.ToArray()) });
            cmd.Parameters.Add(new NpgsqlParameter<long>  { Value = point.ChannelId });
            cmd.Parameters.Add(new NpgsqlParameter<long>  { Value = point.GuildId });
            cmd.Parameters.Add(new NpgsqlParameter<long>  { Value = point.AuthorId });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { Value = point.CreatedAt });
            cmd.Parameters.Add(new NpgsqlParameter<string[]>{ Value = point.Tags.ToArray() });
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(ct);
        logger.LogDebug("Upserted {Count} vector points", points.Count);
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        ReadOnlyMemory<float> query,
        VectorFilter filter,
        int topK,
        CancellationToken ct)
    {
        var built = SearchSqlBuilder.Build(filter, topK);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = built.Sql;

        // @query is always first — added after the filter parameters to match SQL order
        cmd.Parameters.AddWithValue("query", new PgVector(query.ToArray()));

        foreach (var p in built.Parameters)
            cmd.Parameters.AddWithValue(p.Name, p.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<VectorMatch>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new VectorMatch(
                MessageId:  reader.GetInt64(0),
                Score:      (float)reader.GetDouble(1),
                ChannelId:  reader.GetInt64(2),
                GuildId:    reader.GetInt64(3),
                AuthorId:   reader.GetInt64(4),
                CreatedAt:  reader.GetFieldValue<DateTimeOffset>(5),
                Tags:       (reader.GetValue(6) as string[]) ?? Array.Empty<string>()));
        }

        return results;
    }
}
