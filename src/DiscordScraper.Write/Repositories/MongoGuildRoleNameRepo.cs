using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Stub implementation — GuildSagaState does not yet carry role metadata.
///
/// GuildSyncConsumer fetches and stores guild Name and ChannelCount but does not capture
/// guild.roles[]. Until GuildSyncConsumer is extended to persist role id/name pairs on
/// GuildSagaState, there is nothing to query and ProjectMessageConsumer renders all role
/// mentions as "&lt;unknown role&gt;".
/// </summary>
internal sealed class MongoGuildRoleNameRepo(ILogger<MongoGuildRoleNameRepo> logger) : IGuildRoleNameRepo
{
    private static readonly IReadOnlyDictionary<long, string> EmptyResult = new Dictionary<long, string>();

    public Task<IReadOnlyDictionary<long, string>> GetRoleNamesAsync(
        long guildId,
        IReadOnlyCollection<long> roleIds,
        CancellationToken ct)
    {
        if (roleIds.Count > 0)
        {
            // Warn once per call so the gap is visible in logs without being noisy per-message.
            logger.LogWarning(
                "Role name lookup requested for {Count} role(s) in guild {GuildId} but GuildSagaState " +
                "does not carry role metadata yet. All role mentions will render as '<unknown role>'. " +
                "Extend GuildSyncConsumer to capture guild.roles[] to resolve this.",
                roleIds.Count, guildId);
        }

        return Task.FromResult(EmptyResult);
    }
}
