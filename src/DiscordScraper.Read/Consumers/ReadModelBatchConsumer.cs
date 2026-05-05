using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Generic base for read-side batch consumers. Accepts <see cref="Batch{TEvent}"/>, projects
/// each event into zero or more <typeparamref name="TEntity"/> instances via
/// <see cref="IBatchProjector{TEvent,TEntity}"/>, and writes the accumulated set once per batch
/// via <see cref="IBulkWriter{TEntity}"/>.
///
/// <para>
/// <b>Eventual consistency note</b>: each concrete consumer commits its own transaction.
/// A message that fans into five projections (ReadMessage, MessageReference, MessageAttachment,
/// MessageEmbed, MessageTag) will have those rows written by five independent transactions.
/// A query at exactly the wrong moment may see a partial message — ReadMessage exists but its
/// tags have not yet committed. For this read side (eventually consistent, no read-after-publish
/// guarantee), this is acceptable. If per-event atomicity is required in future, collapse the
/// five consumers back into a single multi-table consumer.
/// </para>
/// </summary>
public abstract class ReadModelBatchConsumer<TEvent, TEntity>(
    IBatchProjector<TEvent, TEntity> projector,
    IBulkWriter<TEntity> writer,
    ILogger logger)
    : IConsumer<Batch<TEvent>>
    where TEvent : class
    where TEntity : class
{
    public async Task Consume(ConsumeContext<Batch<TEvent>> context)
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
