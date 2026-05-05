using MassTransit;

namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Published to fan out to all Faulted message sagas, triggering a replay from whichever
/// phase last failed. Optional Phase filter narrows re-entry to a specific leg.
///
/// "tag"      — re-enters from Tagging (sagas with no embedding stored).
/// "classify" — re-enters from Classifying (sagas with embedding but no topic tags).
/// null       — re-enters both phases (each saga picks its own branch via predicate-When).
///
/// The CorrelateBy predicate on the state machine filters to CurrentState == Faulted and
/// the appropriate phase condition. OnMissingInstance discards; fan-out handles routing.
/// </summary>
[ExcludeFromTopology]
public interface MessageReplayRequested
{
    DateTimeOffset Timestamp { get; }

    /// <summary>"tag", "classify", or null for both.</summary>
    string? Phase { get; }
}
