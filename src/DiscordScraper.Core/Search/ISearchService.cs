namespace DiscordScraper.Core.Search;

public interface ISearchService
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
}

public sealed record SearchQuery(
    string Text,
    long? GuildId = null,
    long? ChannelId = null,
    long? AuthorId = null,
    DateTimeOffset? After = null,
    DateTimeOffset? Before = null,
    int TopK = 50,
    int SnippetLength = 240);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalEstimate);

public sealed record SearchHit(
    long MessageId,
    long ChannelId,
    long GuildId,
    long AuthorId,
    DateTimeOffset CreatedAt,
    float Rank,
    string Snippet);
