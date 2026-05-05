using System.Reflection;
using DiscordScraper.Read.Data;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// <see cref="IBulkWriter{TEntity}"/> backed by EFCore.BulkExtensions. Manages its own
/// <see cref="ReadDbContext"/> per call and is safe to resolve as scoped or singleton.
/// </summary>
internal sealed class EfCoreBulkWriter<TEntity>(
    IDbContextFactory<ReadDbContext> factory,
    ILogger<EfCoreBulkWriter<TEntity>> logger)
    : IBulkWriter<TEntity>
    where TEntity : class
{
    public async Task WriteAsync(IEnumerable<TEntity> entities, CancellationToken ct)
    {
        var deduped = DedupeByPrimaryKey(entities);
        if (deduped.Count == 0)
        {
            logger.LogDebug("Batch produced no entities after dedup; skipping write. EntityType={EntityType}", typeof(TEntity).Name);
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BulkInsertOrUpdateAsync(deduped, cancellationToken: ct);
        await tx.CommitAsync(ct);
    }

    private List<TEntity> DedupeByPrimaryKey(IEnumerable<TEntity> entities)
    {
        // Allocate a temporary context only to read PK metadata; never tracked, never saved.
        using var db = factory.CreateDbContext();
        var boxed = entities.Cast<object>().ToList();
        var deduped = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(TEntity), boxed);
        return deduped.Cast<TEntity>().ToList();
    }
}

/// <summary>
/// Static helpers for primary-key deduplication. Separated from the generic class so the
/// logic can be tested directly via <see cref="EfCoreBulkWriterTests"/> through InternalsVisibleTo,
/// and shared across the non-generic and generic code paths without duplication.
/// </summary>
internal static class EfCoreBulkWriterStatics
{
    internal static IList<object> DedupeByPrimaryKey(DbContext db, Type type, IList<object> entities)
    {
        var entityType = db.Model.FindEntityType(type)
            ?? throw new InvalidOperationException($"{type.Name} is not registered in the DbContext model.");
        var keyProps = entityType.FindPrimaryKey()?.Properties
            ?? throw new InvalidOperationException($"{type.Name} has no primary key defined.");

        // Dictionary preserves insertion order (.NET 5+); iterating in batch order means the last
        // occurrence of a given PK naturally overwrites earlier ones — most-recent-wins.
        var byKey = new Dictionary<object, object>();
        foreach (var entity in entities)
        {
            var key = BuildKey(entity, keyProps);
            byKey[key] = entity;
        }

        return byKey.Values.ToList();
    }

    private static object BuildKey(object entity, IReadOnlyList<IReadOnlyProperty> keyProps)
    {
        if (keyProps.Count == 1)
            return keyProps[0].PropertyInfo!.GetValue(entity)!;

        // Composite PK: wrap in a ValueTupleKey so equality is value-based across the tuple.
        var values = new object?[keyProps.Count];
        for (var i = 0; i < keyProps.Count; i++)
            values[i] = keyProps[i].PropertyInfo!.GetValue(entity);
        return new ValueTupleKey(values);
    }

    /// <summary>
    /// Equatable composite-key wrapper. ValueTuple generics require a fixed arity at compile time;
    /// this covers arbitrary-width composite keys at the cost of a small allocation per entity.
    /// </summary>
    private sealed class ValueTupleKey
    {
        private readonly object?[] _values;

        public ValueTupleKey(object?[] values) => _values = values;

        public bool Equals(ValueTupleKey? other) =>
            other is not null
            && _values.Length == other._values.Length
            && _values.Zip(other._values).All(p => Equals(p.First, p.Second));

        public override bool Equals(object? obj) => Equals(obj as ValueTupleKey);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var v in _values) h.Add(v);
            return h.ToHashCode();
        }
    }
}
