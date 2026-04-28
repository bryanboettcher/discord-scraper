using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Write-side canonical guild entity. Persists indefinitely — no terminal state.
/// CorrelationId is derived deterministically so any node receiving a GuildId computes
/// the same value without coordination.
/// </summary>
public sealed class GuildSagaState : SagaStateMachineInstance, ISagaVersion, ITimestamped, GuildModelBase
{
    // MT Mongo repo requires parameterless ctor; all init done by the state machine.
    public GuildSagaState() { }

    public Guid CorrelationId { get; set; }

    /// <summary>MT optimistic concurrency on FindOneAndReplace.</summary>
    public int Version { get; set; }

    /// <summary>InstanceState backing field; bound via <c>InstanceState(x =&gt; x.CurrentState)</c>.</summary>
    public string CurrentState { get; set; } = string.Empty;

    public long GuildId { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateTimeOffset LastSyncedAt { get; set; }
    public int LastSyncChannelCount { get; set; }
}
