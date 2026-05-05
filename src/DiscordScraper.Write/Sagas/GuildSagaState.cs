using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Guild;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Write-side canonical guild entity. Persists indefinitely — no terminal state.
/// CorrelationId is derived deterministically so any node receiving a GuildId computes
/// the same value without coordination.
/// </summary>
public sealed class GuildSagaState : SagaStateMachineInstance, ISagaVersion, ITimestamped
{
    // MT Mongo repo requires parameterless ctor; all init done by the state machine.
    public GuildSagaState() { }

    public Guid CorrelationId { get; set; }

    /// <summary>MT optimistic concurrency on FindOneAndReplace.</summary>
    public int Version { get; set; }

    /// <summary>InstanceState backing field; bound via <c>InstanceState(x =&gt; x.CurrentState)</c>.</summary>
    public string CurrentState { get; set; } = string.Empty;

    public long GuildId { get; set; }

    public DateTimeOffset CreatedOn  { get; set; }
    public DateTimeOffset UpdatedOn  { get; set; }
    public DateTimeOffset? SettledOn  { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateTimeOffset LastSyncedAt { get; set; }
    public int LastSyncChannelCount { get; set; }

    /// <summary>
    /// False when the bot has been removed from the guild or the guild has been deleted.
    /// Heartbeat correlation excludes sagas where IsPresent == false so stale-but-inaccessible
    /// guilds don't re-trigger GuildSyncConsumer indefinitely.
    ///
    /// TODO: flip to false automatically when GuildSyncConsumer encounters a Discord 404/403.
    ///       For v1 the only write path is an explicit admin action. Bot-removal auto-detection
    ///       is a follow-up — the immediate goal is self-registration via POST /api/admin/sync/guilds/{id}.
    /// </summary>
    public bool IsPresent { get; set; } = true;

    /// <summary>
    /// Authoritative role snapshot from the last GuildChanged. Replaced wholesale on each
    /// sync — Discord ships the full list every time. @everyone is excluded before publish.
    /// </summary>
    public IReadOnlyList<GuildRole> Roles { get; set; } = [];
}
