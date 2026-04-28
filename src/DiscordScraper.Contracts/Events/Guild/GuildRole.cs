namespace DiscordScraper.Contracts.Events.Guild;

/// <summary>
/// Snapshot of a Discord role's identity within a guild. Carried on <see cref="GuildChanged"/>
/// and persisted on <see cref="GuildSagaState"/> for role-mention resolution.
/// </summary>
/// <param name="Id">Discord role snowflake.</param>
/// <param name="Name">Display name as returned by the guild REST endpoint.</param>
public sealed record GuildRole(long Id, string Name);
