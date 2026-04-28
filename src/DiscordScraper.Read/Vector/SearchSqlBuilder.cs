using DiscordScraper.Core.Vector;

namespace DiscordScraper.Read.Vector;

/// <summary>
/// Builds parameterized SELECT SQL for vector similarity search.
/// Extracted so filter logic is unit-testable without a live database.
/// </summary>
internal static class SearchSqlBuilder
{
    internal readonly record struct SearchQuery(string Sql, IReadOnlyList<SearchParameter> Parameters);
    internal readonly record struct SearchParameter(string Name, object Value);

    internal static SearchQuery Build(VectorFilter filter, int topK)
    {
        var clauses = new List<string>();
        var parameters = new List<SearchParameter>();

        if (filter.GuildId.HasValue)
        {
            clauses.Add("guild_id = @guild_id");
            parameters.Add(new SearchParameter("guild_id", filter.GuildId.Value));
        }

        if (filter.ChannelId.HasValue)
        {
            clauses.Add("channel_id = @channel_id");
            parameters.Add(new SearchParameter("channel_id", filter.ChannelId.Value));
        }

        if (filter.After.HasValue)
        {
            clauses.Add("created_at > @after");
            parameters.Add(new SearchParameter("after", filter.After.Value));
        }

        if (filter.Before.HasValue)
        {
            clauses.Add("created_at < @before");
            parameters.Add(new SearchParameter("before", filter.Before.Value));
        }

        // tags && @any_tags uses the GIN index; only emitted when the list is non-null and non-empty.
        if (filter.AnyTags is { Count: > 0 } tags)
        {
            clauses.Add("tags && @any_tags");
            parameters.Add(new SearchParameter("any_tags", tags.ToArray()));
        }

        var whereClause = clauses.Count > 0
            ? $"\nWHERE {string.Join("\n  AND ", clauses)}"
            : string.Empty;

        var sql = $"""
            SELECT message_id,
                   1 - (embedding <=> @query) AS score,
                   channel_id,
                   guild_id,
                   author_id,
                   created_at,
                   tags
            FROM message_vectors{whereClause}
            ORDER BY embedding <=> @query
            LIMIT @top_k;
            """;

        parameters.Add(new SearchParameter("top_k", topK));

        return new SearchQuery(sql, parameters);
    }
}
