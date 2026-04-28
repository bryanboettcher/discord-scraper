// ReSharper disable InconsistentNaming
using MassTransit;

namespace DiscordScraper.Contracts.Events.Message;

/// <summary>
/// Intermediate base for all message events. Marked excluded so MT does not create a queue
/// for this layer; consumers subscribe to concrete leaf types or MessageStateChanged.
/// </summary>
[ExcludeFromTopology]
[ExcludeFromImplementedTypes]
public interface BaseMessageEvent : MessageModelBase;
