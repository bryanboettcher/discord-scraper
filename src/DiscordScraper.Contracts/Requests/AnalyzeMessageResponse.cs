namespace DiscordScraper.Contracts.Requests;

public sealed record AnalyzeMessageResponse
{
    public bool IsSubstantive { get; init; }
    public bool IsBot { get; init; }
    public string? DetectedLanguage { get; init; }
}
