// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Guild;

/// <summary>Triggers GuildSyncConsumer to fetch guild metadata, channels, and active threads.</summary>
public interface GuildSyncRequested : BaseGuildEvent;
