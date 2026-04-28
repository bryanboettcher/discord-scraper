// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>
/// Published by ChannelSyncConsumer when a pagination pass finishes.
/// Carries the highest snowflake observed and whether the channel is caught up.
/// The ChannelSagaStateMachine consumes this to advance the cursor and transition
/// state; the read-side ChannelReadConsumer can also subscribe for read_channels updates.
/// </summary>
public interface ChannelSyncCompleted : BaseChannelEvent
{
    /// <summary>Channel display name, stamped from the consumer's in-scope channel context.</summary>
    string Name { get; }

    int ChannelType { get; }

    long? ParentId { get; }

    /// <summary>Highest message snowflake observed in this pass. 0 if no messages were returned.</summary>
    long LastSyncedSnowflake { get; }

    /// <summary>Number of messages emitted via MessageCaptured during this pass.</summary>
    int MessageCount { get; }

    /// <summary>
    /// True when the page returned fewer messages than the page size, indicating no further
    /// history exists beyond the current cursor. Cleared on the next ChannelSyncRequested.
    /// </summary>
    bool IsCaughtUpAtLastPoll { get; }
}
