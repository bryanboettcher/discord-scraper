namespace DiscordScraper.Contracts.Requests;

// Init-only properties (no positional ctor) — see AnalyzeMessageRequest for the rationale.
public sealed record ProjectMessageRequest
{
    public long MessageSnowflake { get; init; }
    public long ChannelId { get; init; }
    public long GuildId { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
}
