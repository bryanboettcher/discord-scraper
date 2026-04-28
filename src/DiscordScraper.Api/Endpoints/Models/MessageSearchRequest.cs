using DiscordScraper.Core.Queries;

namespace DiscordScraper.Api.Endpoints.Models;

/// <summary>
/// Wire contract for POST /api/messages/search.
/// Embedding is intentionally absent: v1 is lexical-only (TSV rank via ISearchService).
/// Semantic hybrid search requires Ollama, which is not wired into the Api host.
/// When hybrid search is added, extend with float[]? Embedding and translate to
/// ReadOnlyMemory&lt;float&gt; before calling IMessageQueryService. Do not add ReadOnlyMemory
/// directly — it does not round-trip through System.Text.Json.
/// </summary>
public sealed record MessageSearchRequest(
    string? Text,
    long? GuildId = null,
    long? ChannelId = null,
    long? AuthorId = null,
    DateTimeOffset? After = null,
    DateTimeOffset? Before = null,
    IReadOnlyList<string>? AnyTags = null,
    int TopK = 50,
    RenderFormat OutputFormat = RenderFormat.Markdown)
{
    public MessageSearchQuery ToQuery() => new(
        TextQuery: Text,
        Embedding: null,   // TODO: hybrid — accept float[]? Embedding from wire, convert here
        GuildId: GuildId,
        ChannelId: ChannelId,
        AuthorId: AuthorId,
        After: After,
        Before: Before,
        AnyTags: AnyTags,
        TopK: TopK,
        OutputFormat: OutputFormat);
}
