using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Reusable batch-projection behavior. Each concrete read-side consumer is a thin
/// <see cref="IConsumer{Batch}"/> wrapper that delegates to <see cref="Project"/>.
///
/// <para>
/// Composition over inheritance. The previous abstract <c>ReadModelBatchConsumer</c>
/// base was scanned by MT's assembly registration and its open-generic abstract type
/// produced runtime "Instances of abstract classes cannot be created" failures during
/// consume. Registering this pipeline as an open generic in DI avoids any abstract
/// IConsumer in the scanned assembly.
/// </para>
///
/// <para>
/// <b>Eventual consistency note</b>: each concrete consumer commits its own transaction.
/// A message that fans into five projections (ReadMessage, MessageReference, MessageAttachment,
/// MessageEmbed, MessageTag) will have those rows written by five independent transactions.
/// A query at exactly the wrong moment may see a partial message — ReadMessage exists but its
/// tags have not yet committed. Acceptable for an eventually-consistent read side.
/// </para>
/// </summary>
public sealed class BatchProjectionPipeline<TEvent, TEntity>(
    IBatchProjector<TEvent, TEntity> projector,
    IBulkWriter<TEntity> writer,
    ILogger<BatchProjectionPipeline<TEvent, TEntity>> logger)
    where TEvent : class
    where TEntity : class
{
    public async Task Project(ConsumeContext<Batch<TEvent>> context)
    {
        var ct = context.CancellationToken;

        var entities = new List<TEntity>();
        foreach (var msg in context.Message)
            entities.AddRange(projector.Project(msg.Message));

        if (entities.Count == 0)
        {
            logger.LogDebug(
                "Batch produced no entities; skipping write. EventType={EventType} EntityType={EntityType}",
                typeof(TEvent).Name, typeof(TEntity).Name);
            return;
        }

        await writer.WriteAsync(entities, ct);

        logger.LogInformation(
            "Batch written. EventType={EventType} EntityType={EntityType} Events={Events} Entities={Entities}",
            typeof(TEvent).Name, typeof(TEntity).Name, context.Message.Length, entities.Count);
    }
}
