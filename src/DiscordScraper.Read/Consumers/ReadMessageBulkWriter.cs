using System.Text.Json;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Idempotent bulk writer for the read_messages hypertable. Uses a session-scoped TEMP TABLE
/// as a staging area: bulk-load the batch into the temp table (UNIQUE(message_id) collapses
/// within-batch duplicates), DELETE matching rows from read_messages by message_id, then INSERT
/// from the temp table so TimescaleDB routes each row to the correct chunk. All three DML
/// operations run inside one transaction.
/// </summary>
internal sealed class ReadMessageBulkWriter(
    IDbContextFactory<ReadDbContext> factory,
    ILogger<ReadMessageBulkWriter> logger)
    : IBulkWriter<ReadMessage>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string Columns =
        "message_id, channel_id, guild_id, author_id, created_at, edited_at, " +
        "reply_to_id, ir, plain_text, has_code, has_attachments, has_embeds, " +
        "is_substantive, is_bot";

    public async Task WriteAsync(IEnumerable<ReadMessage> entities, CancellationToken ct)
    {
        var list = entities.ToList();
        if (list.Count == 0)
        {
            logger.LogDebug("Batch produced no ReadMessage entities; skipping write");
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TEMP TABLE stg_read_messages (
                    message_id      BIGINT      NOT NULL,
                    channel_id      BIGINT      NOT NULL,
                    guild_id        BIGINT      NOT NULL,
                    author_id       BIGINT      NOT NULL,
                    created_at      TIMESTAMPTZ NOT NULL,
                    edited_at       TIMESTAMPTZ,
                    reply_to_id     BIGINT,
                    ir              JSONB       NOT NULL,
                    plain_text      TEXT        NOT NULL,
                    has_code        BOOLEAN     NOT NULL,
                    has_attachments BOOLEAN     NOT NULL,
                    has_embeds      BOOLEAN     NOT NULL,
                    is_substantive  BOOLEAN     NOT NULL,
                    is_bot          BOOLEAN     NOT NULL,
                    UNIQUE (message_id)
                ) ON COMMIT DROP;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await BinaryCopyIntoStagingAsync(conn, tx, list, ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM read_messages
                WHERE message_id = ANY(SELECT message_id FROM stg_read_messages);
                """;
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                logger.LogDebug("Merge: evicted {Deleted} stale read_messages rows", deleted);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO read_messages ({Columns})
                SELECT {Columns} FROM stg_read_messages;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task BinaryCopyIntoStagingAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<ReadMessage> rows,
        CancellationToken ct)
    {
        var deduped = DedupeLastWins(rows);

        await using var writer = await conn.BeginBinaryImportAsync(
            $"COPY stg_read_messages ({Columns}) FROM STDIN (FORMAT BINARY)", ct);

        foreach (var row in deduped)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(row.MessageId,   NpgsqlDbType.Bigint,      ct);
            await writer.WriteAsync(row.ChannelId,   NpgsqlDbType.Bigint,      ct);
            await writer.WriteAsync(row.GuildId,     NpgsqlDbType.Bigint,      ct);
            await writer.WriteAsync(row.AuthorId,    NpgsqlDbType.Bigint,      ct);
            await writer.WriteAsync(row.CreatedAt,   NpgsqlDbType.TimestampTz, ct);
            if (row.EditedAt.HasValue)
                await writer.WriteAsync(row.EditedAt.Value, NpgsqlDbType.TimestampTz, ct);
            else
                await writer.WriteNullAsync(ct);
            if (row.ReplyToId.HasValue)
                await writer.WriteAsync(row.ReplyToId.Value, NpgsqlDbType.Bigint, ct);
            else
                await writer.WriteNullAsync(ct);
            await writer.WriteAsync(
                JsonSerializer.Serialize(row.Ir, JsonOpts), NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(row.PlainText,      NpgsqlDbType.Text,    ct);
            await writer.WriteAsync(row.HasCode,        NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(row.HasAttachments, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(row.HasEmbeds,      NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(row.IsSubstantive,  NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(row.IsBot,          NpgsqlDbType.Boolean, ct);
        }

        await writer.CompleteAsync(ct);
    }

    private static List<ReadMessage> DedupeLastWins(List<ReadMessage> rows)
    {
        var seen = new Dictionary<long, ReadMessage>(rows.Count);
        foreach (var row in rows)
            seen[row.MessageId] = row;
        return [.. seen.Values];
    }
}
