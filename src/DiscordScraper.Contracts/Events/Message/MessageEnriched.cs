// ReSharper disable InconsistentNaming
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Terminal happy-path state. Published when the saga reaches Enriched steady state.
/// Carries the complete enriched snapshot so read consumers do not need to correlate
/// multiple intermediate events (Projected, Enhanced, Indexed) to build a full ReadMessage row.
/// </summary>
public interface MessageEnriched : MessageStateChanged
{
    MessageIR IR { get; }
    IReadOnlyList<string> Tags { get; }
    bool IsSubstantive { get; }
    bool IsBot { get; }
    DateTimeOffset MessageCreatedAt { get; }
    DateTimeOffset? EditedTimestamp { get; }
}
