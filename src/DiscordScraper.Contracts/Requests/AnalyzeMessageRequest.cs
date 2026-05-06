namespace DiscordScraper.Contracts.Requests;

// Positional ctor omitted — MT's anonymous-type initializer (ctx.Init<T>(new {...}))
// requires settable properties, not a positional constructor. Named properties with
// init-only setters satisfy both MT's initializer and C# record-value semantics.
public sealed record AnalyzeMessageRequest : IMeasured
{
    public long MessageSnowflake { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public bool AuthorIsBot { get; init; }

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
