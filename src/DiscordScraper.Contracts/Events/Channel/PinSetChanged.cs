// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>
/// Published by PinPollConsumer after every poll, carrying the canonicalized hash of the
/// current pin set. ChannelSaga compares this against its stored PinSetCanonical to detect
/// changes — no diff logic lives in the consumer.
/// </summary>
public interface PinSetChanged : BaseChannelEvent
{
    /// <summary>
    /// Hex-encoded SHA-256 of the sorted pin snapshot (see PinSetCanonicalizer).
    /// Two polls with the same messages and same edit timestamps produce the same hash.
    /// </summary>
    string CanonicalHash { get; }

    /// <summary>Number of pinned messages at poll time.</summary>
    int PinCount { get; }

    DateTimeOffset ObservedAt { get; }
}
