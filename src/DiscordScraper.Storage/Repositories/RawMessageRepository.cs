using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DiscordScraper.Storage.Repositories;

internal sealed class RawMessageRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IRawMessageRepository
{
    // Parallel arrays via unnest() keep the whole batch to a single round-trip
    // and let Postgres enforce the PK uniquely. ON CONFLICT DO NOTHING makes the
    // call idempotent against replayed Discord pages, which the sync worker can
    // produce during retries or overlapping backfills.
    private const string BulkInsertSql = """
        INSERT INTO raw_messages (message_id, channel_id, guild_id, created_at, fetched_at, payload)
        SELECT * FROM unnest(@message_ids, @channel_ids, @guild_ids, @created_ats, @fetched_ats, @payloads)
        ON CONFLICT (message_id) DO NOTHING
        """;

    public async Task<int> InsertAsync(IReadOnlyList<RawMessageEntity> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return 0;

        var messageIds = new long[messages.Count];
        var channelIds = new long[messages.Count];
        var guildIds = new long[messages.Count];
        var createdAts = new DateTimeOffset[messages.Count];
        var fetchedAts = new DateTimeOffset[messages.Count];
        var payloads = new string[messages.Count];

        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            messageIds[i] = m.MessageId;
            channelIds[i] = m.ChannelId;
            guildIds[i] = m.GuildId;
            createdAts[i] = m.CreatedAt;
            fetchedAts[i] = m.FetchedAt;
            payloads[i] = m.Payload;
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Database.ExecuteSqlRawAsync(
            BulkInsertSql,
            parameters: new object[]
            {
                new NpgsqlParameter("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = messageIds },
                new NpgsqlParameter("channel_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = channelIds },
                new NpgsqlParameter("guild_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = guildIds },
                new NpgsqlParameter("created_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = createdAts },
                new NpgsqlParameter("fetched_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = fetchedAts },
                new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = payloads },
            },
            cancellationToken: ct);
    }

    public async Task<long?> GetMaxMessageIdAsync(long channelId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.RawMessages
            .AsNoTracking()
            .Where(r => r.ChannelId == channelId)
            .OrderByDescending(r => r.MessageId)
            .Select(r => (long?)r.MessageId)
            .FirstOrDefaultAsync(ct);
    }

    public async IAsyncEnumerable<RawMessageEntity> EnumerateUnprojectedAsync(
        int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (batchSize <= 0) yield break;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Keyset pagination on message_id keeps the query bounded regardless
        // of how far behind projection runs. EF translates the Any() predicate
        // into a NOT EXISTS anti-join against messages.
        var cursor = 0L;
        while (!ct.IsCancellationRequested)
        {
            var page = await context.RawMessages
                .AsNoTracking()
                .Where(r => r.MessageId > cursor
                         && !context.Messages.Any(m => m.MessageId == r.MessageId))
                .OrderBy(r => r.MessageId)
                .Take(batchSize)
                .ToListAsync(ct);

            if (page.Count == 0) yield break;

            foreach (var row in page)
                yield return row;

            cursor = page[^1].MessageId;
            if (page.Count < batchSize) yield break;
        }
    }
}
