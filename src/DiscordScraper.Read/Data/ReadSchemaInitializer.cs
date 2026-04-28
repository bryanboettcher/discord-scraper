using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiscordScraper.Read.Data;

/// <summary>
/// Creates the full read-side schema at startup. All DDL uses IF NOT EXISTS / if_not_exists
/// so every statement is idempotent across restarts.
///
/// DDL is structured as a list of discrete statements rather than one big string. This gives:
///   1. Precise error attribution in logs — we know which statement threw.
///   2. Testability — GenerateStatements() is static and unit-testable without a DB.
///   3. Incremental rollout — future phases can append statements without touching existing ones.
///
/// TimescaleDB notes:
///   - create_hypertable: if_not_exists => TRUE supported since TimescaleDB 2.0.
///   - add_compression_policy: if_not_exists => TRUE supported since TimescaleDB 2.6.
///   - add_continuous_aggregate_policy: if_not_exists => TRUE supported since TimescaleDB 2.6.
///   - The continuous aggregate CREATE uses IF NOT EXISTS which handles re-runs cleanly.
///   All targeting timescale/timescaledb-ha:pg17 which ships TimescaleDB 2.x.
/// </summary>
internal sealed class ReadSchemaInitializer(
    NpgsqlDataSource dataSource,
    ILogger<ReadSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring read-model schema is present");
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        var statements = GenerateStatements();
        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed executing read schema DDL statement: {Sql}", sql[..Math.Min(120, sql.Length)]);
                throw;
            }
        }

        logger.LogInformation("Read-model schema ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Returns the ordered list of DDL statements that comprise the read-side schema.
    /// Static so tests can assert specific statement presence without a DB connection.
    /// Ordering matters: hypertable must follow table creation; continuous aggregate must follow hypertable.
    /// </summary>
    public static IReadOnlyList<string> GenerateStatements() =>
    [
        // Extensions
        "CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;",
        "CREATE EXTENSION IF NOT EXISTS vector;",

        // ----------------------------------------------------------------
        // read_messages — hypertable partition key: created_at
        // tsv is a GENERATED ALWAYS AS stored column; DB computes it.
        // No physical PK — logical uniqueness on message_id enforced by
        // idempotent-insert discipline at the consumer level.
        // ----------------------------------------------------------------
        """
        CREATE TABLE IF NOT EXISTS read_messages (
            message_id      BIGINT NOT NULL,
            channel_id      BIGINT NOT NULL,
            guild_id        BIGINT NOT NULL,
            author_id       BIGINT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL,
            edited_at       TIMESTAMPTZ,
            reply_to_id     BIGINT,
            ir              JSONB NOT NULL,
            plain_text      TEXT NOT NULL,
            tsv             TSVECTOR GENERATED ALWAYS AS (to_tsvector('english', plain_text)) STORED,
            has_code        BOOLEAN NOT NULL,
            has_attachments BOOLEAN NOT NULL,
            has_embeds      BOOLEAN NOT NULL,
            is_substantive  BOOLEAN NOT NULL,
            is_bot          BOOLEAN NOT NULL
        );
        """,

        // Hypertable — idempotent via if_not_exists parameter
        "SELECT create_hypertable('read_messages', 'created_at', chunk_time_interval => INTERVAL '7 days', if_not_exists => TRUE);",

        // Indexes on read_messages
        "CREATE INDEX IF NOT EXISTS read_messages_message_id_idx ON read_messages (message_id);",
        "CREATE INDEX IF NOT EXISTS read_messages_channel_created_idx ON read_messages (channel_id, created_at DESC);",
        "CREATE INDEX IF NOT EXISTS read_messages_author_idx ON read_messages (author_id);",
        "CREATE INDEX IF NOT EXISTS read_messages_tsv_idx ON read_messages USING GIN (tsv);",

        // TimescaleDB compression
        """
        DO $$
        BEGIN
            ALTER TABLE read_messages SET (timescaledb.compress, timescaledb.compress_segmentby = 'channel_id');
        EXCEPTION WHEN OTHERS THEN
            -- Compression options already set; ignore duplicate-setting errors on re-run.
            NULL;
        END;
        $$;
        """,

        "SELECT add_compression_policy('read_messages', INTERVAL '30 days', if_not_exists => TRUE);",

        // ----------------------------------------------------------------
        // Extraction tables — no FK constraints (logical relation only)
        // ----------------------------------------------------------------
        """
        CREATE TABLE IF NOT EXISTS message_references (
            message_id BIGINT   NOT NULL,
            kind       SMALLINT NOT NULL,
            target_id  BIGINT,
            ordinal    SMALLINT NOT NULL,
            PRIMARY KEY (message_id, ordinal)
        );
        """,

        "CREATE INDEX IF NOT EXISTS message_references_target_idx ON message_references (kind, target_id);",

        """
        CREATE TABLE IF NOT EXISTS message_attachments (
            message_id    BIGINT NOT NULL,
            attachment_id BIGINT NOT NULL,
            url           TEXT   NOT NULL,
            content_type  TEXT,
            size_bytes    BIGINT,
            PRIMARY KEY (message_id, attachment_id)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS message_embeds (
            message_id  BIGINT   NOT NULL,
            embed_index SMALLINT NOT NULL,
            embed_type  TEXT,
            url         TEXT,
            title       TEXT,
            description TEXT,
            PRIMARY KEY (message_id, embed_index)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS message_tags (
            message_id BIGINT NOT NULL,
            tag        TEXT   NOT NULL,
            PRIMARY KEY (message_id, tag)
        );
        """,

        "CREATE INDEX IF NOT EXISTS message_tags_tag_idx ON message_tags (tag);",

        // ----------------------------------------------------------------
        // Entity context tables
        // ----------------------------------------------------------------
        """
        CREATE TABLE IF NOT EXISTS read_channels (
            channel_id BIGINT PRIMARY KEY,
            guild_id   BIGINT NOT NULL,
            name       TEXT   NOT NULL,
            type       SMALLINT NOT NULL,
            parent_id  BIGINT,
            topic      TEXT,
            updated_at TIMESTAMPTZ NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS read_guilds (
            guild_id   BIGINT PRIMARY KEY,
            name       TEXT   NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL
        );
        """,

        // ----------------------------------------------------------------
        // Continuous aggregate — must come after hypertable exists
        // ----------------------------------------------------------------
        """
        CREATE MATERIALIZED VIEW IF NOT EXISTS messages_per_channel_per_hour
        WITH (timescaledb.continuous) AS
        SELECT
            channel_id,
            time_bucket('1 hour', created_at) AS bucket,
            count(*) AS message_count
        FROM read_messages
        GROUP BY channel_id, bucket;
        """,

        """
        SELECT add_continuous_aggregate_policy('messages_per_channel_per_hour',
            start_offset      => INTERVAL '7 days',
            end_offset        => INTERVAL '1 hour',
            schedule_interval => INTERVAL '1 hour',
            if_not_exists     => TRUE);
        """,
    ];
}
