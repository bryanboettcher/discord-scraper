// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Guild;

[ExcludeFromTopology]
[ExcludeFromImplementedTypes]
public interface BaseGuildEvent : GuildModelBase;
