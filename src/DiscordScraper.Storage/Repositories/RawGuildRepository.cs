using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage.Repositories;

internal sealed class RawGuildRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IRawGuildRepository
{
    public async Task<string?> GetCurrentPayloadAsync(long guildId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.GuildsCurrent
            .AsNoTracking()
            .Where(g => g.GuildId == guildId)
            .Select(g => g.Payload)
            .FirstOrDefaultAsync(ct);
    }

    public async Task InsertSnapshotAsync(RawGuildEntity snapshot, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.RawGuilds.Add(snapshot);
        await context.SaveChangesAsync(ct);
    }
}
