// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>Clock-tick fact that a channel sync pass is due. ChannelSyncConsumer paginates from the given cursor.</summary>
public interface ChannelSyncDue : BaseChannelEvent
{
    long? CursorSnowflake { get; }
}
