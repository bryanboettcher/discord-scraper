namespace DiscordScraper.Contracts.Requests;

// Init-only properties (no positional ctor) — ctx.Init<T> with anonymous objects requires
// property-bag init, not positional args. See AnalyzeMessageRequest for the canonical example.
public sealed record TagMessageRequest : IMeasured
{
    public long MessageSnowflake { get; init; }
    public string PlainText { get; init; } = string.Empty;

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
