using DiscordScraper.Write.Sagas;
using MongoDB.Driver;

namespace DiscordScraper.Write.Repositories;

/// <summary>
/// Fetches channel display names from the channel_sagas collection via an indexed $in query.
/// One network round-trip per ProjectMessageConsumer invocation regardless of ref count.
/// </summary>
internal sealed class MongoChannelNameRepo(IMongoDatabase db) : IChannelNameRepo
{
    // Must match the collection name registered for ChannelSagaState in SagaRegistrationExtensions.
    private const string CollectionName = "channel_sagas";

    private static readonly IReadOnlyDictionary<long, string> EmptyResult = new Dictionary<long, string>();

    private static readonly ProjectionDefinition<ChannelSagaState> Projection =
        Builders<ChannelSagaState>.Projection
            .Include(s => s.ChannelId)
            .Include(s => s.Name);

    public async Task<IReadOnlyDictionary<long, string>> GetNamesAsync(
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

        var dict = new Dictionary<long, string>(results.Count);
        foreach (var s in results)
        {
            if (!string.IsNullOrEmpty(s.Name))
                dict[s.ChannelId] = s.Name;
        }

        return dict;
    }
}
