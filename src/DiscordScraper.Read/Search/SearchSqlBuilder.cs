using DiscordScraper.Core.Search;
using Npgsql;

namespace DiscordScraper.Read.Search;

/// <summary>
/// Builds parameterized SQL for full-text search against read_messages.tsv.
/// Extracted so filter assembly is unit-testable without a live database.
///
/// The query CTE pattern (FROM read_messages, websearch_to_tsquery(...) AS query) lets
/// both the WHERE clause and the SELECT list reference the same query object by alias,
/// avoiding redundant evaluation of websearch_to_tsquery.
/// </summary>
internal static class SearchSqlBuilder
{
    internal readonly record struct BuiltQuery(string MainSql, string CountSql, NpgsqlParameter[] Parameters);

    internal static BuiltQuery Build(SearchQuery query)
    {
        // MaxWords=0 breaks ts_headline; very large values inflate query planning time.
        // Divide-by-10 maps the user-facing character count to a word budget for ts_headline.
        var snippetLength = Math.Clamp(query.SnippetLength, 60, 1000);

        var clauses = new List<string> { "tsv @@ query" };
        var parameters = new List<NpgsqlParameter>
        {
            new NpgsqlParameter<string>("text", query.Text)
        };

        if (query.GuildId.HasValue)
        {
            clauses.Add("guild_id = @guild_id");
            parameters.Add(new NpgsqlParameter<long>("guild_id", query.GuildId.Value));
        }

        if (query.ChannelId.HasValue)
        {
            clauses.Add("channel_id = @channel_id");
            parameters.Add(new NpgsqlParameter<long>("channel_id", query.ChannelId.Value));
        }

        if (query.AuthorId.HasValue)
        {
            clauses.Add("author_id = @author_id");
            parameters.Add(new NpgsqlParameter<long>("author_id", query.AuthorId.Value));
        }

        if (query.After.HasValue)
        {
            clauses.Add("created_at > @after");
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("after", query.After.Value));
        }

        if (query.Before.HasValue)
        {
            clauses.Add("created_at < @before");
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("before", query.Before.Value));
        }

        var whereClause = string.Join("\n  AND ", clauses);

        // ts_headline config string embeds snippetLength directly — it is a display hint
        // capped by MaxWords, not user input reaching SQL, so string interpolation is safe here.
        // MaxFragments=2 keeps context around multiple match sites; MinWords=5 prevents stubs.
        var headlineConfig = $"MaxFragments=2, MaxWords={snippetLength / 10}, MinWords=5";

        var mainSql = $"""
            SELECT
                message_id,
                channel_id,
                guild_id,
                author_id,
                created_at,
                ts_rank(tsv, query) AS rank,
                ts_headline('english', plain_text, query, '{headlineConfig}') AS snippet
            FROM read_messages,
                 websearch_to_tsquery('english', @text) AS query
            WHERE {whereClause}
            ORDER BY rank DESC
            LIMIT @top_k;
            """;

        var countSql = $"""
            SELECT COUNT(*)
            FROM read_messages,
                 websearch_to_tsquery('english', @text) AS query
            WHERE {whereClause};
            """;

        // top_k added last so it appears after filter params (mirrors vector builder convention)
        parameters.Add(new NpgsqlParameter<int>("top_k", query.TopK));

        return new BuiltQuery(mainSql, countSql, parameters.ToArray());
    }
}
