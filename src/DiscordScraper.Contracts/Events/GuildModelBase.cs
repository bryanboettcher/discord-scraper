// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events;

public interface GuildModelBase : CorrelatedBy<Guid>, ITimestamped
{
    long GuildId { get; }
    string CurrentState { get; }

    /// <summary>Timestamp of the last saga state transition that produced this event.</summary>
    new DateTimeOffset UpdatedOn { get; }

    new Guid CorrelationId => DeterministicGuid.FromSnowflake(GuildId);
}
