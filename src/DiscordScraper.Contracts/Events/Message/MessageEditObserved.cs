// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published by PinPollConsumer or gateway when a message edit is detected.</summary>
public interface MessageEditObserved : BaseMessageEvent
{
    DateTimeOffset EditedAt { get; }
    string UpdatedPayloadJson { get; }
}
