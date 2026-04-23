namespace DiscordScraper.Storage.Repositories;

/// <summary>
/// Observability log for worker runs. One row per <c>ExecuteAsync</c> pass so
/// the admin UI / SRE can see what ran, how long it took, and what failed.
/// </summary>
public interface IIngestionRunRepository
{
    /// <summary>Inserts a new 'running' row and returns its id.</summary>
    Task<long> StartAsync(string worker, CancellationToken ct = default);

    Task MarkSuccessAsync(long id, int itemsProcessed, CancellationToken ct = default);

    Task MarkFailedAsync(long id, int itemsProcessed, string error, CancellationToken ct = default);
}
