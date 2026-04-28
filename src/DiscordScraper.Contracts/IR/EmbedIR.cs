namespace DiscordScraper.Contracts.IR;

public sealed record EmbedIR(
    int Index,
    string? Type,
    string? Url,
    string? Title,
    string? Description);
