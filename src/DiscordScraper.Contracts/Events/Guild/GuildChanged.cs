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
}
