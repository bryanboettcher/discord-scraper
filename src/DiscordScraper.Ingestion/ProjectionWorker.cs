using System.Globalization;
using System.Text.Json;
using DiscordScraper.Ingestion.Options;
using DiscordScraper.Ingestion.Projection;
using DiscordScraper.Storage;
using DiscordScraper.Storage.Entities;
using DiscordScraper.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Ingestion;

/// <summary>
/// Rebuilds the Tier 2 <c>messages</c> projection from <c>raw_messages</c> on
/// a polling loop. Coordinates with the sync worker only through Postgres: it
/// finds un-projected raw rows via an anti-join and upserts projected rows
/// idempotently, so a TRUNCATE + rerun always reproduces the same result.
/// </summary>
public sealed class ProjectionWorker(
    IDbContextFactory<DiscordScraperDbContext> contextFactory,
    IRawMessageRepository rawMessageRepo,
    IMessageRepository messageRepo,
    IIngestionRunRepository runRepo,
    IOptions<ProjectionOptions> options,
    ILogger<ProjectionWorker> logger) : BackgroundService
{
    private const string WorkerName = "projection";

    private readonly ProjectionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Projection worker starting; batch={Batch}, interval={Interval}",
            _options.BatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var runId = await runRepo.StartAsync(WorkerName, stoppingToken);
            var itemsProcessed = 0;

            try
            {
                itemsProcessed = await RunPassAsync(stoppingToken);
                await runRepo.MarkSuccessAsync(runId, itemsProcessed, stoppingToken);
                if (itemsProcessed > 0)
                    logger.LogInformation("Projection pass: {Count} messages projected", itemsProcessed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await runRepo.MarkFailedAsync(
                    runId, itemsProcessed, "cancelled during shutdown", CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Projection pass crashed");
                await runRepo.MarkFailedAsync(runId, itemsProcessed, ex.Message, CancellationToken.None);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        var context = await BuildContextAsync(ct);

        var batch = new List<MessageEntity>(_options.BatchSize);
        var total = 0;

        await foreach (var raw in rawMessageRepo.EnumerateUnprojectedAsync(_options.BatchSize, ct))
        {
            batch.Add(MessageProjector.Project(raw, context));

            if (batch.Count >= _options.BatchSize)
            {
                total += await messageRepo.UpsertBatchAsync(batch, ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            total += await messageRepo.UpsertBatchAsync(batch, ct);
            batch.Clear();
        }

        return total;
    }

    // -------------------------------------------------------------------------
    // Context load: materialize once per pass so each message's resolution is
    // an in-memory dictionary lookup.
    // -------------------------------------------------------------------------

    private async Task<ProjectionContext> BuildContextAsync(CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var channelRows = await context.ChannelsCurrent.AsNoTracking().ToListAsync(ct);
        var channels = new Dictionary<long, ChannelInfo>(channelRows.Count);
        foreach (var row in channelRows)
        {
            using var doc = JsonDocument.Parse(row.Payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            var parentId = TryGetSnowflake(root, "parent_id");
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";
            channels[row.ChannelId] = new ChannelInfo(row.ChannelId, type, parentId, name);
        }

        var guildRows = await context.GuildsCurrent.AsNoTracking().ToListAsync(ct);
        var guildRoles = new Dictionary<long, IReadOnlyDictionary<long, string>>(guildRows.Count);
        foreach (var row in guildRows)
        {
            using var doc = JsonDocument.Parse(row.Payload);
            var roles = new Dictionary<long, string>();
            if (doc.RootElement.TryGetProperty("roles", out var rolesArr) && rolesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in rolesArr.EnumerateArray())
                {
                    if (!role.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    var id = long.Parse(idEl.GetString()!, CultureInfo.InvariantCulture);
                    var name = role.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "" : "";
                    roles[id] = name;
                }
            }
            guildRoles[row.GuildId] = roles;
        }

        logger.LogDebug(
            "Projection context loaded: {Channels} channels, {Guilds} guilds",
            channels.Count, guildRoles.Count);

        return new ProjectionContext(channels, guildRoles);
    }

    private static long? TryGetSnowflake(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var e)) return null;
        if (e.ValueKind != JsonValueKind.String) return null;
        var str = e.GetString();
        return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
