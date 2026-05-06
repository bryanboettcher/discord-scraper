using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Requests;

public sealed record ProjectMessageResponse(MessageIR IR) : IStampable
{
    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }
}
