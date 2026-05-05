// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events;

/// <summary>
/// Write-side identity contract for all message events and saga state.
/// MessageSnowflake is the raw Discord ID; MessageId is its deterministic Guid form kept
/// as a convenience property so consumers don't recompute it per-event. Both are present
/// because the saga correlation key must be a Guid (MT requirement) while all downstream
/// Discord API calls and read-model joins key on the snowflake.
/// </summary>
public interface MessageModelBase : CorrelatedBy<Guid>, ITimestamped
{
    /// <summary>Deterministic Guid derived from MessageSnowflake. Equals CorrelationId.</summary>
    Guid MessageId { get; }

    /// <summary>Raw Discord message snowflake. Use for Discord API calls and read-model joins.</summary>
    long MessageSnowflake { get; }

    long ChannelId { get; }
    long GuildId { get; }
    long AuthorId { get; }
    string CurrentState { get; }

    /// <summary>Timestamp of the last saga state transition that produced this event.</summary>
    new DateTimeOffset UpdatedOn { get; }

    // Derived from snowflake so any node can compute it without coordination.
    new Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageSnowflake);
}
