using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Storage.Repositories;

internal sealed class SyncStateRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : ISyncStateRepository
{
    public async Task<RawSyncStateEntity?> GetAsync(long channelId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.RawSyncState
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ChannelId == channelId, ct);
    }

    public async Task UpsertAsync(RawSyncStateEntity state, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var existing = await context.RawSyncState
            .FirstOrDefaultAsync(s => s.ChannelId == state.ChannelId, ct);

        if (existing is null)
        {
            context.RawSyncState.Add(state);
        }
        else
        {
            existing.LastMessageId = state.LastMessageId;
            existing.LastSyncedAt = state.LastSyncedAt;
            existing.LastError = state.LastError;
            existing.ConsecutiveErrors = state.ConsecutiveErrors;
        }

        await context.SaveChangesAsync(ct);
    }

    public async Task RecordErrorAsync(long channelId, string error, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var rows = await context.RawSyncState
            .Where(s => s.ChannelId == channelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.ConsecutiveErrors, x => x.ConsecutiveErrors + 1)
                .SetProperty(x => x.LastSyncedAt, DateTimeOffset.UtcNow),
            ct);

        if (rows == 0)
        {
            // No row yet — seed one so the error is still observable.
            await UpsertAsync(new RawSyncStateEntity
            {
                ChannelId = channelId,
                LastMessageId = 0,
                LastSyncedAt = DateTimeOffset.UtcNow,
                LastError = error,
                ConsecutiveErrors = 1
            }, ct);
        }
    }
}
