using DiscordScraper.Write.Sagas;
using MongoDB.Driver;

namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Fetches sync cursors (LastSyncedSnowflake) from channel_sagas via an indexed $in query.
/// Single round-trip regardless of how many channels GuildSyncConsumer fans out to.
/// </summary>
internal sealed class MongoChannelCursorRepo(IMongoDatabase db) : IChannelCursorRepo
{
    private const string CollectionName = "channel_sagas";

    private static readonly IReadOnlyDictionary<long, long> EmptyResult = new Dictionary<long, long>();

    private static readonly ProjectionDefinition<ChannelSagaState> Projection =
        Builders<ChannelSagaState>.Projection
            .Include(s => s.ChannelId)
            .Include(s => s.LastSyncedSnowflake);

    public async Task<IReadOnlyDictionary<long, long>> GetCursorsAsync(
        IReadOnlyCollection<long> channelIds,
        CancellationToken ct)
    {
        if (channelIds.Count == 0)
            return EmptyResult;

        var collection = db.GetCollection<ChannelSagaState>(CollectionName);
        var filter = Builders<ChannelSagaState>.Filter.In(s => s.ChannelId, channelIds);

        var results = await collection
            .Find(filter)
            .Project<ChannelSagaState>(Projection)
            .ToListAsync(ct);

        var dict = new Dictionary<long, long>(results.Count);
        foreach (var s in results)
            dict[s.ChannelId] = s.LastSyncedSnowflake;

        return dict;
    }
}
