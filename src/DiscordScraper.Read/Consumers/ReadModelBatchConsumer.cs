using DiscordScraper.Read.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Base class for read-side batch consumers. Accepts <see cref="Batch{TEvent}"/>, projects
/// each event into one or more EF entities via <see cref="Project"/>, and writes them all
/// inside a single transaction per batch — atomic visibility per batch.
///
/// Multi-table writes (e.g., a single MessageStateChanged producing ReadMessage +
/// MessageReference + MessageAttachment + MessageEmbed + MessageTag) are handled by returning
/// all entities from a single <see cref="Project"/> call. The base routes each entity to the
/// correct table by runtime type via <see cref="IReadBulkWriter"/>.
/// </summary>
public abstract class ReadModelBatchConsumer<TEvent> : IConsumer<Batch<TEvent>>
    where TEvent : class
{
    private readonly IDbContextFactory<ReadDbContext> _factory;
    private readonly IReadBulkWriter _writer;
    private readonly ILogger _logger;

    protected ReadModelBatchConsumer(
        IDbContextFactory<ReadDbContext> factory,
        IReadBulkWriter writer,
        ILogger logger)
    {
        _factory = factory;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>
    /// Projects a single event into the entities that should be upserted.
    /// Return all entity types in a single call; the base accumulates and routes by runtime type.
    /// </summary>
    protected abstract IEnumerable<object> Project(TEvent evt);

    public async Task Consume(ConsumeContext<Batch<TEvent>> context)
    {
        var ct = context.CancellationToken;

        var byType = new Dictionary<Type, IList<object>>();
        foreach (var msg in context.Message)
        {
            foreach (var entity in Project(msg.Message))
            {
                var type = entity.GetType();
                if (!byType.TryGetValue(type, out var bucket))
                {
                    bucket = [];
                    byType[type] = bucket;
                }
                bucket.Add(entity);
            }
        }

        if (byType.Count == 0)
        {
            _logger.LogDebug("Batch produced no entities; skipping write. EventType={EventType}", typeof(TEvent).Name);
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await _writer.WriteAsync(db, tx, byType, ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Batch written. EventType={EventType} Count={Count}",
            typeof(TEvent).Name,
            context.Message.Length);
    }
}
