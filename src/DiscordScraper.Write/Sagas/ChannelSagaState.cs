using DiscordScraper.Contracts;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Write-side canonical channel entity. Persists indefinitely — no terminal state.
/// CorrelationId is deterministic from ChannelId so any node receiving a snowflake
/// computes the same value without coordination.
/// </summary>
public sealed class ChannelSagaState : SagaStateMachineInstance, ISagaVersion, ITimestamped
{
    // MT Mongo repo requires parameterless ctor; all init done by the state machine.
    public ChannelSagaState() { }

    public Guid CorrelationId { get; set; }

    /// <summary>MT optimistic concurrency on FindOneAndReplace.</summary>
    public int Version { get; set; }

    /// <summary>InstanceState backing field; bound via <c>InstanceState(x =&gt; x.CurrentState)</c>.</summary>
    public string CurrentState { get; set; } = string.Empty;

    public long ChannelId { get; set; }
    public long GuildId { get; set; }

    public DateTimeOffset CreatedOn  { get; set; }
    public DateTimeOffset UpdatedOn  { get; set; }
    public DateTimeOffset? SettledOn  { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Discord's numeric channel type (0 = text, 5 = announcement, 10/11/12 = thread variants).</summary>
    public int ChannelType { get; set; }

    /// <summary>For threads: the parent text channel snowflake. Null for top-level channels.</summary>
    public long? ParentId { get; set; }

    // --- Sync cursor ---

    /// <summary>
    /// Highest snowflake from the last completed sync pass. 0 means no sync has run yet.
    /// Stamped onto the next ChannelSyncDue so the consumer resumes without querying Mongo.
    /// </summary>
    public long LastSyncedSnowflake { get; set; }

    public DateTimeOffset LastSyncedAt { get; set; }

    /// <summary>
    /// Transient observation — true when the last sync page was short (fewer than page-size),
    /// indicating the channel is caught up with Discord's history. Not a terminal state.
    /// </summary>
    public bool IsCaughtUpAtLastPoll { get; set; }

    /// <summary>Number of MessageCaptured events published in the last sync pass.</summary>
    public int LastSyncMessageCount { get; set; }

    // --- Pin polling ---

    /// <summary>Canonicalized hash of the last-observed pin set. Null until the first pin poll.</summary>
    public string? PinSetCanonical { get; set; }

    /// <summary>
    /// MT scheduler token for the pending PinPollDue message. Cancelled on reschedule so duplicate
    /// polls can't accumulate if the saga re-enters CaughtUp before the scheduled message fires.
    /// </summary>
    public Guid? PinPollScheduleId { get; set; }

    /// <summary>
    /// False if Discord returned 404/410 for this channel. Grace-period archival policy is deferred;
    /// the state machine leaves this field settable but does not act on it in v1.
    /// </summary>
    public bool IsPresent { get; set; } = true;
}
