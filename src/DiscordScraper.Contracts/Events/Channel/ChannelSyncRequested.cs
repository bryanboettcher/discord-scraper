// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Channel;

/// <summary>Triggers ChannelSyncConsumer to paginate messages from the given cursor.</summary>
public interface ChannelSyncRequested : BaseChannelEvent
{
    long? CursorSnowflake { get; }
}
