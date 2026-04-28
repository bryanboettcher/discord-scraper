// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Channel;

[ExcludeFromTopology]
[ExcludeFromImplementedTypes]
public interface BaseChannelEvent : ChannelModelBase;
