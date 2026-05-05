using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Creates indexes on saga collections at startup. CreateIndex with the same key is a
/// MongoDB no-op, so every restart is safe. These are required because CorrelateBy predicate
/// scans (e.g. TagsInvalidated fan-out, MessageReplayRequested) would otherwise do full
/// collection scans on message_sagas and guild_sagas.
/// </summary>
internal sealed class SagaIndexInitializer(
    IMongoDatabase db,
    ILogger<SagaIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureCurrentStateIndex("message_sagas", cancellationToken);
        await EnsureCurrentStateIndex("guild_sagas", cancellationToken);
        await EnsureCurrentStateIndex("channel_sagas", cancellationToken);
        logger.LogInformation("Saga CurrentState indexes ensured");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureCurrentStateIndex(string collectionName, CancellationToken ct)
    {
        var collection = db.GetCollection<MongoDB.Bson.BsonDocument>(collectionName);
        var keys = Builders<MongoDB.Bson.BsonDocument>.IndexKeys.Ascending("CurrentState");
        var model = new CreateIndexModel<MongoDB.Bson.BsonDocument>(
            keys,
            new CreateIndexOptions { Background = true, Name = "ix_CurrentState" });

        try
        {
            await collection.Indexes.CreateOneAsync(model, cancellationToken: ct);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "IndexOptionsConflict" || ex.CodeName == "IndexKeySpecsConflict")
        {
            // Index with a different name but same key spec already exists — no action needed.
            logger.LogDebug("CurrentState index on {Collection} already exists (different name): {Message}",
                collectionName, ex.Message);
        }
    }
}
