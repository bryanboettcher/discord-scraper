// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Sync;

/// <summary>
/// Periodic tick from SyncSchedulerService. Saga state machines correlate this event
/// against their own staleness criteria (e.g. GuildSagaStateMachine matches sagas whose
/// LastSyncedAt is older than the heartbeat's StaleAfter cutoff).
///
/// MT's CorrelateBy dispatches one heartbeat to N matching sagas, serially. At v1 scale
/// (≲100 guilds) this is fine; at hyperscale a partitioned heartbeat per shard would
/// distribute the fan-out.
///
/// Mongo path: 1 Find (collection scan on staleness + state filter) + N serial
/// FindOneAndReplace. Index on (CurrentState, LastSyncedAt) amortises the scan cost.
/// </summary>
[ExcludeFromTopology]
public interface SyncHeartbeat
{
    /// <summary>When the heartbeat was emitted.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Sagas with LastSyncedAt &lt; this cutoff are considered stale and should re-sync.
    /// Computed as Timestamp - SyncInterval by the scheduler.
    /// </summary>
    DateTimeOffset StaleAfter { get; }
}
