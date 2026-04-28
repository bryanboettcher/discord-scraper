// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Message;

/// <summary>Published by ChannelSyncConsumer when a raw Discord message is ingested.</summary>
public interface MessageCaptured : BaseMessageEvent
{
    string PayloadJson { get; }
    bool AuthorIsBot { get; }
    string? HomeChannelName { get; }
}
