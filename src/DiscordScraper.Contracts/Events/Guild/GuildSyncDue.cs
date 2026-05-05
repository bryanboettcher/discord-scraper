// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Guild;

/// <summary>Clock-tick fact that a guild sync pass is due. GuildSyncConsumer fetches guild metadata, channels, and active threads.</summary>
public interface GuildSyncDue : BaseGuildEvent;
