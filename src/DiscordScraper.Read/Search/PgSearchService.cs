using DiscordScraper.Core.Search;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiscordScraper.Read.Search;

/// <summary>
/// Postgres TSV implementation of ISearchService. Migration corridor: swap for Tantivy/Meilisearch
/// behind this same interface without touching callers.
///
/// TotalEstimate uses SELECT COUNT(*) with the same WHERE clause as the main query.
/// For v1 this is acceptable — count scans on TimescaleDB hypertables are parallelised and
/// the GIN index makes the tsv @@ query predicate cheap. Replace with EXPLAIN-based row estimates
/// if counts become slow at high message volumes (> ~10M rows in a single time range).
/// </summary>
internal sealed class PgSearchService(NpgsqlDataSource dataSource, ILogger<PgSearchService> logger) : ISearchService
{
    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var built = SearchSqlBuilder.Build(query);

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var hits = await ExecuteMainQueryAsync(conn, built, ct);
        var total = await ExecuteCountQueryAsync(conn, built, ct);

        if (hits.Count == 0)
            logger.LogDebug("Full-text search for {Text:l} returned no results", query.Text);

        return new SearchResult(hits, total);
    }

    private static async Task<List<SearchHit>> ExecuteMainQueryAsync(
        NpgsqlConnection conn,
        SearchSqlBuilder.BuiltQuery built,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = built.MainSql;

        // Clone parameters — each command owns its parameter instances
        foreach (var p in built.Parameters)
            cmd.Parameters.Add(CloneParameter(p));

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var hits = new List<SearchHit>();
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new SearchHit(
                MessageId:  reader.GetInt64(0),
                ChannelId:  reader.GetInt64(1),
                GuildId:    reader.GetInt64(2),
                AuthorId:   reader.GetInt64(3),
                CreatedAt:  reader.GetFieldValue<DateTimeOffset>(4),
                Rank:       (float)reader.GetDouble(5),
                Snippet:    reader.GetString(6)));
        }

        return hits;
    }

    private static async Task<int> ExecuteCountQueryAsync(
        NpgsqlConnection conn,
        SearchSqlBuilder.BuiltQuery built,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // COUNT query doesn't use top_k — exclude it (last parameter by convention)
        cmd.CommandText = built.CountSql;

        foreach (var p in built.Parameters)
        {
            if (p.ParameterName == "top_k") continue;
            cmd.Parameters.Add(CloneParameter(p));
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter source) =>
        new() { ParameterName = source.ParameterName, Value = source.Value };
}
