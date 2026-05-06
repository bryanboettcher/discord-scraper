using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Requests;

public sealed record ProjectMessageResponse(MessageIR IR) : IMeasured
{
    /// <inheritdoc cref="IStampable.Timestamp"/>
    public DateTimeOffset Timestamp { get; set; }

    /// <inheritdoc cref="IMeasured.ReceivedOn"/>
    public DateTimeOffset ReceivedOn { get; set; }
}
