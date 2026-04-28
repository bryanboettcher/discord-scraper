using DiscordScraper.Core.Queries;
using DiscordScraper.Read.Data;
using DiscordScraper.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiscordScraper.Read.Queries;

internal sealed class ChannelQueryService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<ReadDbContext> dbFactory,
    ILogger<ChannelQueryService> logger) : IChannelQueryService
{
    /// <summary>
    /// Queries the messages_per_channel_per_hour TimescaleDB continuous aggregate.
    /// Sums message_count buckets that fall within the requested time window, then
    /// joins to read_channels to resolve the channel name.
    ///
    /// Raw Npgsql is used here because the aggregate view is not an EF-mapped entity.
    /// </summary>
    private const string ActiveChannelsSql = """
        SELECT
            m.channel_id,
            c.name,
            SUM(m.message_count)::int AS total_count,
            MAX(m.bucket) + INTERVAL '1 hour' AS most_recent
        FROM messages_per_channel_per_hour m
        JOIN read_channels c ON c.channel_id = m.channel_id
        WHERE c.guild_id = @guild_id
          AND m.bucket >= NOW() - (@since_hours * INTERVAL '1 hour')
        GROUP BY m.channel_id, c.name
        ORDER BY total_count DESC
        """;

    public async Task<IReadOnlyList<ChannelSummary>> GetActiveChannelsAsync(
        long guildId, int sinceHours, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = ActiveChannelsSql;
        cmd.Parameters.AddWithValue("guild_id",    guildId);
        cmd.Parameters.AddWithValue("since_hours", sinceHours);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<ChannelSummary>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ChannelSummary(
                ChannelId:             reader.GetInt64(0),
                Name:                  reader.GetString(1),
                MessageCountInWindow:  reader.GetInt32(2),
                MostRecentActivity:    reader.GetFieldValue<DateTimeOffset>(3)));
        }

        if (results.Count == 0)
            logger.LogDebug("GetActiveChannelsAsync for guild {GuildId} last {Hours}h returned no results",
                guildId, sinceHours);

        return results;
    }

    public async Task<IReadOnlyList<RenderedMessage>> GetRecentMessagesAsync(
        long channelId, int count, RenderFormat format, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var messages = await db.ReadMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(count)
            .ToListAsync(ct);

        if (messages.Count == 0)
            return [];

        var messageIds = messages.Select(m => m.MessageId).ToArray();

        // Materialize first: EF InMemory and some providers can't translate GroupBy unless
        // it is composed into a SQL aggregate. Client-side grouping is correct here.
        var rawTags = await db.MessageTags
            .AsNoTracking()
            .Where(t => messageIds.Contains(t.MessageId))
            .Select(t => new { t.MessageId, t.Tag })
            .ToListAsync(ct);

        var tagMap = rawTags
            .GroupBy(t => t.MessageId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => t.Tag).ToList());

        var channelName = await db.ReadChannels
            .AsNoTracking()
            .Where(c => c.ChannelId == channelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct) ?? channelId.ToString();

        var ctx = await RenderContextLoader.LoadManyAsync(messages.Select(m => m.Ir), db, ct);

        var rendered = new List<RenderedMessage>(messages.Count);
        foreach (var msg in messages)
        {
            var tags = tagMap.TryGetValue(msg.MessageId, out var t) ? t : (IReadOnlyList<string>)[];
            var body = MessageRenderer.Render(msg.Ir, format, ctx);
            rendered.Add(new RenderedMessage(
                MessageId:   msg.MessageId,
                ChannelId:   msg.ChannelId,
                GuildId:     msg.GuildId,
                AuthorId:    msg.AuthorId,
                CreatedAt:   msg.CreatedAt,
                EditedAt:    msg.EditedAt,
                Body:        body,
                Tags:        tags,
                AuthorName:  msg.AuthorId.ToString(),
                ChannelName: channelName));
        }

        return rendered;
    }
}
