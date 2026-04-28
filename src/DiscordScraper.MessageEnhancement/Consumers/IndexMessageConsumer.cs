using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.MessageEnhancement.Consumers;

public sealed class IndexMessageConsumer(
    IVectorStore vectorStore,
    ISystemClock clock,
    ILogger<IndexMessageConsumer> logger) : IConsumer<IndexMessageRequest>
{
    public async Task Consume(ConsumeContext<IndexMessageRequest> context)
    {
        var req = context.Message;

        var point = new VectorPoint(
            MessageId: req.MessageSnowflake,
            Embedding: req.Embedding.ToArray().AsMemory(),
            ChannelId: req.ChannelId,
            GuildId: req.GuildId,
            AuthorId: req.AuthorId,
            CreatedAt: req.CreatedAt,
            Tags: req.Tags);

        await vectorStore.UpsertManyAsync([point], context.CancellationToken);

        var indexedAt = clock.UtcNow;

        await context.RespondAsync<IndexMessageResponse>(new
        {
            IndexedAt = indexedAt,
        });

        logger.LogInformation(
            "Indexed {MessageSnowflake} guild={GuildId} channel={ChannelId} tags={TagCount} dims={Dims}",
            req.MessageSnowflake, req.GuildId, req.ChannelId, req.Tags.Count, req.Embedding.Count);
    }
}
