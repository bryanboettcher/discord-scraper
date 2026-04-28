using DiscordScraper.Read.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DiscordScraper.Api.Admin;

internal sealed class PgReadStoreStatistics(
    IDbContextFactory<ReadDbContext> dbFactory,
    NpgsqlDataSource dataSource) : IReadStoreStatistics
{
    public async Task<ReadStoreCounts> GetCountsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var messageCount = await db.ReadMessages.LongCountAsync(ct);
        var channelCount = await db.ReadChannels.LongCountAsync(ct);
        var guildCount   = await db.ReadGuilds.LongCountAsync(ct);

        // message_vectors is owned by PgVectorStore via raw Npgsql — not in the EF context.
        await using var cmd = dataSource.CreateCommand("SELECT COUNT(*) FROM message_vectors");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var vectorCount = reader.GetInt64(0);

        return new ReadStoreCounts(messageCount, channelCount, guildCount, vectorCount);
    }
}
