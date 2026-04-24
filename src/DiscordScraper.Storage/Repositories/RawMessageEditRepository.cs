using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DiscordScraper.Storage.Repositories;

internal sealed class RawMessageEditRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IRawMessageEditRepository
{
    private const string InsertSql = """
        INSERT INTO raw_message_edits (message_id, edited_at, fetched_at, payload)
        SELECT * FROM unnest(@message_ids, @edited_ats, @fetched_ats, @payloads)
        ON CONFLICT (message_id, edited_at) DO NOTHING
        """;

    public async Task<int> InsertIfNewAsync(IReadOnlyList<RawMessageEditEntity> edits, CancellationToken ct = default)
    {
        if (edits.Count == 0) return 0;

        var count = edits.Count;
        var messageIds = new long[count];
        var editedAts = new DateTimeOffset[count];
        var fetchedAts = new DateTimeOffset[count];
        var payloads = new string[count];

        for (var i = 0; i < count; i++)
        {
            var e = edits[i];
            messageIds[i] = e.MessageId;
            editedAts[i] = e.EditedAt;
            fetchedAts[i] = e.FetchedAt;
            payloads[i] = e.Payload;
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Database.ExecuteSqlRawAsync(
            InsertSql,
            parameters: new object[]
            {
                new NpgsqlParameter("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = messageIds },
                new NpgsqlParameter("edited_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = editedAts },
                new NpgsqlParameter("fetched_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = fetchedAts },
                new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = payloads },
            },
            cancellationToken: ct);
    }
}
