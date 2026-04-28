namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Role display-name lookup for <c>ProjectMessageConsumer</c>. <see cref="MongoGuildRoleNameRepo"/>
/// is a stub — see that class for the gap. Until <c>GuildSyncConsumer</c> persists guild.roles[]
/// onto <c>GuildSagaState</c>, role mentions render as "&lt;unknown role&gt;".
/// </summary>
public interface IGuildRoleNameRepo
{
    /// <summary>
    /// Returns a roleId → name map for the requested role IDs within the guild. Missing IDs are
    /// absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> GetRoleNamesAsync(
        long guildId,
        IReadOnlyCollection<long> roleIds,
        CancellationToken ct);
}
