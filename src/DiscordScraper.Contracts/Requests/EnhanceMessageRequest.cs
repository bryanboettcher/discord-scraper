using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Requests;

// Init-only properties (no positional ctor) — see AnalyzeMessageRequest for the rationale.
public sealed record EnhanceMessageRequest
{
    public long MessageSnowflake { get; init; }
    public MessageIR IR { get; init; } = null!;
    public string PlainText { get; init; } = string.Empty;
}
