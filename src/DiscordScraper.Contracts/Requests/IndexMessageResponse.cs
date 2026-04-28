namespace DiscordScraper.Contracts.Requests;

// Positional ctor omitted — MT's anonymous-type initializer used in RespondAsync requires
// settable properties. Consistent with other response types (AnalyzeMessageResponse).
public sealed record IndexMessageResponse
{
    public DateTimeOffset IndexedAt { get; init; }
}
