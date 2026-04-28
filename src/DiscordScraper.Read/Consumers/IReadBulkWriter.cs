using DiscordScraper.Read.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Abstracts EFCore.BulkExtensions upsert dispatch so the base consumer can be tested
/// without a live Postgres instance. The concrete implementation uses reflection-based
/// MakeGenericMethod dispatch because BulkInsertOrUpdateAsync has no non-generic overload.
/// </summary>
public interface IReadBulkWriter
{
    /// <summary>
    /// Upserts all entities in <paramref name="entitiesByType"/> within <paramref name="transaction"/>.
    /// Each entry is a strongly-typed IList cast to IList&lt;object&gt; by the base consumer;
    /// the concrete implementation re-casts via reflection before calling BulkInsertOrUpdateAsync&lt;T&gt;.
    /// </summary>
    Task WriteAsync(
        ReadDbContext db,
        IDbContextTransaction transaction,
        IReadOnlyDictionary<Type, IList<object>> entitiesByType,
        CancellationToken ct);
}
