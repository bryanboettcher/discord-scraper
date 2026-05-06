namespace DiscordScraper.Contracts.Requests;

public sealed record AnalyzeMessageResponse : IMeasured
{
    public bool IsSubstantive { get; init; }
    public bool IsBot { get; init; }
    public string? DetectedLanguage { get; init; }

    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
