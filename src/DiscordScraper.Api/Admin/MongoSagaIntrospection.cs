using MongoDB.Bson;
using MongoDB.Driver;

namespace DiscordScraper.Api.Admin;

/// <summary>
/// Reads saga collections as raw BsonDocuments so Api doesn't depend on DiscordScraper.Write.
/// Field names mirror the saga state C# properties — Mongo's default serializer uses the
/// property name as the BSON key (no attribute overrides in the saga states).
/// </summary>
internal sealed class MongoSagaIntrospection(IMongoDatabase db) : ISagaIntrospection
{
    private static readonly string[] CountGroupPipeline =
    [
        "{ $group: { _id: '$CurrentState', count: { $sum: 1 } } }"
    ];

    private IMongoCollection<BsonDocument> Guilds   => db.GetCollection<BsonDocument>("guild_sagas");
    private IMongoCollection<BsonDocument> Channels => db.GetCollection<BsonDocument>("channel_sagas");
    private IMongoCollection<BsonDocument> Messages => db.GetCollection<BsonDocument>("message_sagas");

    public async Task<SagaCountsByState> GetCountsAsync(CancellationToken ct)
    {
        var guildTask   = AggregateCountsAsync(Guilds, ct);
        var channelTask = AggregateCountsAsync(Channels, ct);
        var messageTask = AggregateCountsAsync(Messages, ct);

        await Task.WhenAll(guildTask, channelTask, messageTask);

        return new SagaCountsByState(
            await guildTask,
            await channelTask,
            await messageTask);
    }

    public async Task<IReadOnlyList<GuildSagaSnapshot>> ListGuildSagasAsync(CancellationToken ct)
    {
        var projection = Builders<BsonDocument>.Projection
            .Include("GuildId")
            .Include("Name")
            .Include("CurrentState")
            .Include("UpdatedOn")
            .Include("LastSyncedAt")
            .Include("LastSyncChannelCount")
            .Include("Roles");

        var docs = await Guilds.Find(FilterDefinition<BsonDocument>.Empty)
            .Project(projection)
            .ToListAsync(ct);

        return docs.Select(MapGuildSnapshot).ToList();
    }

    public async Task<IReadOnlyList<ChannelSagaSnapshot>> ListChannelSagasAsync(long? guildId, CancellationToken ct)
    {
        var filter = guildId.HasValue
            ? Builders<BsonDocument>.Filter.Eq("GuildId", guildId.Value)
            : FilterDefinition<BsonDocument>.Empty;

        var projection = Builders<BsonDocument>.Projection
            .Include("ChannelId")
            .Include("GuildId")
            .Include("Name")
            .Include("ChannelType")
            .Include("CurrentState")
            .Include("LastSyncedSnowflake")
            .Include("LastSyncedAt")
            .Include("IsCaughtUpAtLastPoll")
            .Include("LastSyncMessageCount")
            .Include("PinSetCanonical");

        var docs = await Channels.Find(filter)
            .Project(projection)
            .ToListAsync(ct);

        return docs.Select(MapChannelSnapshot).ToList();
    }

    public async Task<MessageSagaSnapshot?> GetMessageSagaAsync(long messageSnowflake, CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("MessageSnowflake", messageSnowflake);

        var doc = await Messages.Find(filter).FirstOrDefaultAsync(ct);
        return doc is null ? null : MapMessageSnapshot(doc);
    }

    private static async Task<IReadOnlyDictionary<string, long>> AggregateCountsAsync(
        IMongoCollection<BsonDocument> collection, CancellationToken ct)
    {
        var pipeline = CountGroupPipeline
            .Select(BsonDocument.Parse)
            .ToList();

        var cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct);
        var docs = await cursor.ToListAsync(ct);

        // Mongo $sum returns Int32 for small counts and Int64 only when the running total
        // exceeds int.MaxValue. ToInt64() handles both representations.
        return docs.ToDictionary(
            d => d["_id"].AsString,
            d => d["count"].ToInt64());
    }

    private static GuildSagaSnapshot MapGuildSnapshot(BsonDocument d) => new(
        GuildId:              d["GuildId"].AsInt64,
        Name:                 d.GetValueOrDefault("Name", string.Empty),
        CurrentState:         d.GetValueOrDefault("CurrentState", string.Empty),
        UpdatedOn:            d.Contains("UpdatedOn") && !d["UpdatedOn"].IsBsonNull
                                  ? d["UpdatedOn"].ToNullableUniversalTime()
                                  : null,
        LastSyncedAt:         d["LastSyncedAt"].ToUniversalTime(),
        LastSyncChannelCount: d["LastSyncChannelCount"].AsInt32,
        RoleCount:            d.Contains("Roles") && d["Roles"].IsBsonArray
                                  ? d["Roles"].AsBsonArray.Count
                                  : 0);

    private static ChannelSagaSnapshot MapChannelSnapshot(BsonDocument d) => new(
        ChannelId:             d["ChannelId"].AsInt64,
        GuildId:               d["GuildId"].AsInt64,
        Name:                  d.GetValueOrDefault("Name", string.Empty),
        ChannelType:           d["ChannelType"].AsInt32,
        CurrentState:          d.GetValueOrDefault("CurrentState", string.Empty),
        LastSyncedSnowflake:   d["LastSyncedSnowflake"].AsInt64,
        LastSyncedAt:          d["LastSyncedAt"].ToUniversalTime(),
        IsCaughtUpAtLastPoll:  d["IsCaughtUpAtLastPoll"].AsBoolean,
        LastSyncMessageCount:  d["LastSyncMessageCount"].AsInt32,
        PinSetCanonical:       d.Contains("PinSetCanonical") && !d["PinSetCanonical"].IsBsonNull
                                   ? d["PinSetCanonical"].AsString
                                   : null);

    private static MessageSagaSnapshot MapMessageSnapshot(BsonDocument d) => new(
        MessageSnowflake: d["MessageSnowflake"].AsInt64,
        ChannelId:        d["ChannelId"].AsInt64,
        GuildId:          d["GuildId"].AsInt64,
        AuthorId:         d["AuthorId"].AsInt64,
        AuthorIsBot:      d["AuthorIsBot"].AsBoolean,
        CurrentState:     d.GetValueOrDefault("CurrentState", string.Empty),
        UpdatedOn:        d.Contains("UpdatedOn") && !d["UpdatedOn"].IsBsonNull
                              ? d["UpdatedOn"].ToNullableUniversalTime()
                              : null,
        MessageCreatedAt: d["MessageCreatedAt"].ToUniversalTime(),
        EditedTimestamp:  d.Contains("EditedTimestamp") && !d["EditedTimestamp"].IsBsonNull
                              ? d["EditedTimestamp"].ToUniversalTime()
                              : null,
        HasPendingEdit:   d.Contains("HasPendingEdit") && d["HasPendingEdit"].AsBoolean,
        IsSubstantive:    d.Contains("IsSubstantive") && !d["IsSubstantive"].IsBsonNull
                              ? d["IsSubstantive"].AsBoolean
                              : null,
        IsBot:            d.Contains("IsBot") && !d["IsBot"].IsBsonNull
                              ? d["IsBot"].AsBoolean
                              : null,
        DetectedLanguage: d.Contains("DetectedLanguage") && !d["DetectedLanguage"].IsBsonNull
                              ? d["DetectedLanguage"].AsString
                              : null,
        Tags:             d.Contains("Tags") && d["Tags"].IsBsonArray
                              ? d["Tags"].AsBsonArray.Select(t => t.AsString).ToList()
                              : null,
        IndexedAt:        d.Contains("IndexedAt") && !d["IndexedAt"].IsBsonNull
                              ? d["IndexedAt"].ToUniversalTime()
                              : null);
}

file static class BsonDocumentExtensions
{
    public static string GetValueOrDefault(this BsonDocument doc, string key, string defaultValue)
        => doc.Contains(key) && !doc[key].IsBsonNull ? doc[key].AsString : defaultValue;

    public static DateTimeOffset? ToNullableUniversalTime(this BsonValue value)
        => value.IsBsonNull ? null : (DateTimeOffset)value.ToUniversalTime();
}
