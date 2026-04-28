namespace DiscordScraper.Api.Admin;

public interface IReadStoreStatistics
{
    Task<ReadStoreCounts> GetCountsAsync(CancellationToken ct);
}
