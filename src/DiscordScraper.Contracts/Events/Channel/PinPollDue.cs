// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>
/// Scheduled trigger for pin polling. ChannelSaga enqueues this via MT Schedule{};
/// PinPollConsumer receives it, fetches /channels/{id}/pins, and publishes PinSetChanged.
///
/// [ExcludeFromTopology] keeps MT from creating a durable subscriber queue for this event;
/// it is consumed only by PinPollConsumer via the Schedule{} endpoint routing.
/// </summary>
[ExcludeFromTopology]
public interface PinPollDue : ChannelModelBase
{
    DateTimeOffset DueAt { get; }
}
