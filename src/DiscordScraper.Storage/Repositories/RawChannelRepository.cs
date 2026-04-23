using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage.Repositories;

internal sealed class RawChannelRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IRawChannelRepository
{
    public async Task<string?> GetCurrentPayloadAsync(long channelId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.ChannelsCurrent
            .AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
    }

    public async Task InsertSnapshotAsync(RawChannelEntity snapshot, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.RawChannels.Add(snapshot);
        await context.SaveChangesAsync(ct);
    }
}
