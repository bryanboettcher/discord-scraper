namespace DiscordScraper.Contracts.Requests;

// Init-only properties (no positional ctor) — see AnalyzeMessageRequest for the rationale.
// AuthorId and CreatedAt come from MessageSagaState so IndexMessageConsumer can build a
// VectorPoint without an extra round-trip; the saga must populate both when issuing this request.
public sealed record IndexMessageRequest
{
    public long MessageSnowflake { get; init; }
    public long GuildId { get; init; }
    public long ChannelId { get; init; }
    public long AuthorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<float> Embedding { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}
