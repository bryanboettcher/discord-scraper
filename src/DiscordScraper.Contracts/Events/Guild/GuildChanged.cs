// ReSharper disable InconsistentNaming
namespace DiscordScraper.Contracts.Events.Guild;

/// <summary>Published when Discord reports a guild metadata update.</summary>
public interface GuildChanged : BaseGuildEvent
{
    string Name { get; }

    /// <summary>
    /// Authoritative role list at time of fetch. @everyone is excluded — it is not a
    /// mentionable target; the @everyone mention path uses a separate MentionKind.
    /// </summary>
    IReadOnlyList<GuildRole> Roles { get; }

    /// <summary>
    /// False when the bot received 403 or 404 for this guild, indicating the bot was removed
    /// or the guild was deleted. Defaults to true so existing publishers don't need to set it.
    /// </summary>
    bool IsPresent { get; }
}
