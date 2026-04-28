// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events;

public interface ChannelModelBase : CorrelatedBy<Guid>, ITimestamped
{
    long ChannelId { get; }
    long GuildId { get; }
    string CurrentState { get; }

    new Guid CorrelationId => DeterministicGuid.FromSnowflake(ChannelId);
}
