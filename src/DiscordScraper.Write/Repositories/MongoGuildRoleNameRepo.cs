using DiscordScraper.Write.Sagas;
using MongoDB.Driver;

namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Resolves role display names from the Roles array persisted on GuildSagaState.
/// One network round-trip per guild regardless of how many role ids are requested.
/// </summary>
internal sealed class MongoGuildRoleNameRepo(IMongoDatabase db) : IGuildRoleNameRepo
{
    // Must match the collection name in SagaRegistrationExtensions.
    private const string CollectionName = "guild_sagas";

    private static readonly IReadOnlyDictionary<long, string> EmptyResult = new Dictionary<long, string>();

    // Project only GuildId and Roles — the saga document can be large (channels, cursors, etc.).
    private static readonly ProjectionDefinition<GuildSagaState> Projection =
        Builders<GuildSagaState>.Projection
            .Include(s => s.GuildId)
            .Include(s => s.Roles);

    public async Task<IReadOnlyDictionary<long, string>> GetRoleNamesAsync(
        long guildId,
        IReadOnlyCollection<long> roleIds,
        CancellationToken ct)
    {
        if (roleIds.Count == 0)
            return EmptyResult;

        var collection = db.GetCollection<GuildSagaState>(CollectionName);
        var filter = Builders<GuildSagaState>.Filter.Eq(s => s.GuildId, guildId);

        var state = await collection
            .Find(filter)
            .Project<GuildSagaState>(Projection)
            .FirstOrDefaultAsync(ct);

        if (state is null || state.Roles.Count == 0)
            return EmptyResult;

        var idSet = roleIds.ToHashSet();
        var dict = new Dictionary<long, string>();
        foreach (var role in state.Roles)
        {
            if (idSet.Contains(role.Id) && !string.IsNullOrEmpty(role.Name))
                dict[role.Id] = role.Name;
        }

        return dict;
    }
}
