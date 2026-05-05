// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events;

public interface ChannelModelBase : CorrelatedBy<Guid>, ITimestamped
{
    long ChannelId { get; }
    long GuildId { get; }
    string CurrentState { get; }

    /// <summary>Timestamp of the last saga state transition that produced this event.</summary>
    new DateTimeOffset UpdatedOn { get; }

    new Guid CorrelationId => DeterministicGuid.FromSnowflake(ChannelId);
}
