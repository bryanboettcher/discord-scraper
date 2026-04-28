// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events;

public interface GuildModelBase : CorrelatedBy<Guid>, ITimestamped
{
    long GuildId { get; }
    string CurrentState { get; }

    new Guid CorrelationId => DeterministicGuid.FromSnowflake(GuildId);
}
