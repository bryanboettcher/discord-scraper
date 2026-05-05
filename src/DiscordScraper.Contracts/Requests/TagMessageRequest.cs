namespace DiscordScraper.Contracts.Requests;

// Init-only properties (no positional ctor) — ctx.Init<T> with anonymous objects requires
// property-bag init, not positional args. See AnalyzeMessageRequest for the canonical example.
public sealed record TagMessageRequest
{
    public long MessageSnowflake { get; init; }
    public string PlainText { get; init; } = string.Empty;
}
