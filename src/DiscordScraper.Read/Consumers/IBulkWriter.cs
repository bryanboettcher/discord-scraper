namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Bulk-upserts a homogeneous collection of <typeparamref name="TEntity"/> rows.
/// Implementations handle deduplication by primary key and transaction management.
/// </summary>
public interface IBulkWriter<TEntity> where TEntity : class
{
    Task WriteAsync(IEnumerable<TEntity> entities, CancellationToken ct);
}
