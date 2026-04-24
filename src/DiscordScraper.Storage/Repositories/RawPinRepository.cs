using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage.Repositories;

internal sealed class RawPinRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IRawPinRepository
{
    public async Task<string?> GetCurrentPayloadAsync(long channelId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.PinsCurrent
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId)
            .Select(p => p.Payload)
            .FirstOrDefaultAsync(ct);
    }

    public async Task InsertSnapshotAsync(RawPinEntity snapshot, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.RawPins.Add(snapshot);
        await context.SaveChangesAsync(ct);
    }
}
