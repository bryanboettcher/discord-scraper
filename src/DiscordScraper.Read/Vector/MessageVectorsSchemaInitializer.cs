using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiscordScraper.Read.Vector;

/// <summary>
/// Creates message_vectors and its supporting indexes on startup. Bootstrap DDL only — a real
/// migration story is deferred per <c>.planning/architecture-plan.md</c>. The HNSW index uses
/// vector_cosine_ops because all queries use the cosine distance operator (<see cref="!:&lt;=&gt;"/>).
/// </summary>
internal sealed class MessageVectorsSchemaInitializer(
    NpgsqlDataSource dataSource,
    ILogger<MessageVectorsSchemaInitializer> logger) : IHostedService
{
    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS message_vectors (
            message_id BIGINT PRIMARY KEY,
            embedding  VECTOR(768) NOT NULL,
            channel_id BIGINT NOT NULL,
            guild_id   BIGINT NOT NULL,
            author_id  BIGINT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            tags       TEXT[] NOT NULL DEFAULT '{}'
        );

        CREATE INDEX IF NOT EXISTS message_vectors_channel_idx
            ON message_vectors (channel_id);

        CREATE INDEX IF NOT EXISTS message_vectors_guild_idx
            ON message_vectors (guild_id);

        CREATE INDEX IF NOT EXISTS message_vectors_created_idx
            ON message_vectors (created_at);

        CREATE INDEX IF NOT EXISTS message_vectors_tags_idx
            ON message_vectors USING GIN (tags);

        CREATE INDEX IF NOT EXISTS message_vectors_embedding_hnsw
            ON message_vectors USING hnsw (embedding vector_cosine_ops);
        """;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring message_vectors schema is present");
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Ddl;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("message_vectors schema ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
