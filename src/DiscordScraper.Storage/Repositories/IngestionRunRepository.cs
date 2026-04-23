using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage.Repositories;

internal sealed class IngestionRunRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IIngestionRunRepository
{
    public async Task<long> StartAsync(string worker, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var entity = new IngestionRunEntity
        {
            Worker = worker,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
        };
        context.IngestionRuns.Add(entity);
        await context.SaveChangesAsync(ct);
        return entity.Id;
    }

    public Task MarkSuccessAsync(long id, int itemsProcessed, CancellationToken ct = default) =>
        CompleteAsync(id, itemsProcessed, status: "success", error: null, ct);

    public Task MarkFailedAsync(long id, int itemsProcessed, string error, CancellationToken ct = default) =>
        CompleteAsync(id, itemsProcessed, status: "failed", error, ct);

    private async Task CompleteAsync(long id, int itemsProcessed, string status, string? error, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        await context.IngestionRuns
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ItemsProcessed, itemsProcessed)
                .SetProperty(r => r.Error, error),
            ct);
    }
}
