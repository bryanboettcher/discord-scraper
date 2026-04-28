// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>Published when Discord reports a channel metadata update (name, topic, etc.).</summary>
public interface ChannelChanged : BaseChannelEvent
{
    string Name { get; }
    string? Topic { get; }
}
