namespace DiscordScraper.Contracts.IR;

public sealed record AttachmentIR(
    long Id,
    string Url,
    string? ContentType,
    long? SizeBytes,
    string? Filename);
