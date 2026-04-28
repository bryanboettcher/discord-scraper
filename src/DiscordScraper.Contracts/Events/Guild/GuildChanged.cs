// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Guild;

/// <summary>Published when Discord reports a guild metadata update.</summary>
public interface GuildChanged : BaseGuildEvent
{
    string Name { get; }
}
