using DiscordScraper.Discord;
using DiscordScraper.Discord.Models;
using DiscordScraper.Discord.Options;
using DiscordScraper.Storage.Entities;
using DiscordScraper.Storage.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Ingestion;

/// <summary>
/// Pulls Discord REST data into the Tier 1 raw_* tables on a polling interval.
/// Snapshots guild/channel payloads only when a stable-field subset drifts so
/// the append-only log doesn't grow unboundedly. Messages are captured once and
/// the cursor (<c>raw_sync_state.last_message_id</c>) advances per batch flush
/// so a crash mid-pass only costs the current batch, not the whole channel.
/// </summary>
public sealed class DiscordSyncWorker(
    IDiscordClient discord,
    IRawGuildRepository guildRepo,
    IRawChannelRepository channelRepo,
    IRawMessageRepository messageRepo,
    IRawPinRepository pinRepo,
    IRawMessageEditRepository editRepo,
    ISyncStateRepository syncStateRepo,
    IIngestionRunRepository runRepo,
    IOptions<DiscordOptions> options,
    ILogger<DiscordSyncWorker> logger) : BackgroundService
{
    private const string WorkerName = "sync";
    private const int MessageBatchSize = 100;

    // Discord channel types we ingest. 0=text, 5=announcement, 15=forum.
    // 10/11/12 are thread types, discovered separately via the threads endpoints.
    // 2=voice, 4=category, 13=stage, 14=directory are skipped entirely.
    private static readonly HashSet<int> TextLikeChannelTypes = [0, 5, 15];

    private readonly DiscordOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Discord sync worker starting; guilds={Guilds}, interval={Interval}",
            string.Join(",", _options.Guilds), _options.SyncInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var runId = await runRepo.StartAsync(WorkerName, stoppingToken);
            var itemsProcessed = 0;

            try
            {
                foreach (var guildId in _options.Guilds)
                {
                    try
                    {
                        itemsProcessed += await ProcessGuildAsync(guildId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Isolate guild failures so a single unreachable guild
                        // doesn't abort syncing of the others.
                        logger.LogError(ex, "Sync failed for guild {GuildId}", guildId);
                    }
                }

                await runRepo.MarkSuccessAsync(runId, itemsProcessed, stoppingToken);
                logger.LogInformation(
                    "Sync pass complete: {Items} messages captured", itemsProcessed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Close the run row so the ingestion_runs table doesn't keep
                // "running" rows across restarts after hard shutdowns. Use a
                // fresh CT because the worker's own token has already fired.
                await runRepo.MarkFailedAsync(
                    runId, itemsProcessed, "cancelled during shutdown", CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync pass crashed");
                await runRepo.MarkFailedAsync(runId, itemsProcessed, ex.Message, CancellationToken.None);
            }

            try
            {
                await Task.Delay(_options.SyncInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Per-guild pass
    // -------------------------------------------------------------------------

    private async Task<int> ProcessGuildAsync(string guildIdString, CancellationToken ct)
    {
        var guild = await discord.GetGuildAsync(guildIdString, ct);
        logger.LogInformation("Syncing guild {GuildId} ({Name})", guild.GuildId, guild.Name);

        await SnapshotGuildIfChangedAsync(guild, ct);

        var channels = await discord.GetGuildChannelsAsync(guildIdString, ct);
        var textChannels = channels
            .Where(c => TextLikeChannelTypes.Contains(c.Type))
            .ToArray();

        foreach (var channel in textChannels)
            await SnapshotChannelIfChangedAsync(channel, ct);

        var activeThreads = await discord.GetGuildActiveThreadsAsync(guildIdString, ct);
        foreach (var thread in activeThreads)
            await SnapshotChannelIfChangedAsync(thread, ct);

        // Archived threads are listed per parent channel, not per guild. The
        // bot's channel-level permissions vary — role overrides can deny
        // READ_MESSAGE_HISTORY on individual channels and return 403 here.
        // Isolate per-channel so one locked channel doesn't sink the pass.
        var archivedThreads = new List<DiscordChannelRaw>();
        foreach (var channel in textChannels)
        {
            try
            {
                await foreach (var thread in discord.EnumerateArchivedThreadsAsync(channel.ChannelId.ToString(), ct))
                {
                    await SnapshotChannelIfChangedAsync(thread, ct);
                    archivedThreads.Add(thread);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogDebug(
                    "Channel {ChannelId} ({Name}): archived threads forbidden, skipping",
                    channel.ChannelId, channel.Name);
            }
        }

        var sources = textChannels
            .Concat(activeThreads)
            .Concat(archivedThreads)
            .ToArray();

        logger.LogInformation(
            "Guild {GuildId}: {Channels} channels + {Active} active threads + {Archived} archived threads",
            guild.GuildId, textChannels.Length, activeThreads.Count, archivedThreads.Count);

        var totalInserted = 0;
        foreach (var source in sources)
        {
            totalInserted += await SyncChannelMessagesAsync(source, guild.GuildId, ct);
            await SyncChannelPinsAsync(source, guild.GuildId, ct);
        }
        return totalInserted;
    }

    // -------------------------------------------------------------------------
    // Snapshot-on-change
    // -------------------------------------------------------------------------

    private async Task SnapshotGuildIfChangedAsync(DiscordGuildRaw guild, CancellationToken ct)
    {
        var existing = await guildRepo.GetCurrentPayloadAsync(guild.GuildId, ct);
        if (existing is not null &&
            PayloadCanonicalization.GuildCanonical(existing) ==
            PayloadCanonicalization.GuildCanonical(guild.Payload))
        {
            return;
        }

        await guildRepo.InsertSnapshotAsync(new RawGuildEntity
        {
            GuildId = guild.GuildId,
            FetchedAt = DateTimeOffset.UtcNow,
            Payload = guild.Payload,
        }, ct);

        logger.LogInformation(
            "Guild {GuildId} snapshot written ({Reason})",
            guild.GuildId, existing is null ? "first sighting" : "stable fields drifted");
    }

    private async Task SnapshotChannelIfChangedAsync(DiscordChannelRaw channel, CancellationToken ct)
    {
        var existing = await channelRepo.GetCurrentPayloadAsync(channel.ChannelId, ct);
        if (existing is not null &&
            PayloadCanonicalization.ChannelCanonical(existing) ==
            PayloadCanonicalization.ChannelCanonical(channel.Payload))
        {
            return;
        }

        await channelRepo.InsertSnapshotAsync(new RawChannelEntity
        {
            ChannelId = channel.ChannelId,
            GuildId = channel.GuildId,
            FetchedAt = DateTimeOffset.UtcNow,
            Payload = channel.Payload,
        }, ct);

        logger.LogDebug(
            "Channel {ChannelId} ({Name}) snapshot written ({Reason})",
            channel.ChannelId, channel.Name, existing is null ? "first sighting" : "stable fields drifted");
    }

    // -------------------------------------------------------------------------
    // Per-channel / per-thread message sync
    // -------------------------------------------------------------------------

    private async Task<int> SyncChannelMessagesAsync(DiscordChannelRaw source, long guildId, CancellationToken ct)
    {
        var state = await syncStateRepo.GetAsync(source.ChannelId, ct);
        var cursor = state?.LastMessageId ?? 0L;

        var batch = new List<RawMessageEntity>(MessageBatchSize);
        var totalInserted = 0;

        try
        {
            await foreach (var msg in discord.EnumerateChannelMessagesAsync(
                               source.ChannelId.ToString(), guildId, cursor, ct))
            {
                batch.Add(new RawMessageEntity
                {
                    MessageId = msg.MessageId,
                    ChannelId = msg.ChannelId,
                    GuildId = msg.GuildId,
                    CreatedAt = msg.CreatedAt,
                    FetchedAt = DateTimeOffset.UtcNow,
                    Payload = msg.Payload,
                });

                if (batch.Count >= MessageBatchSize)
                    totalInserted += await FlushBatchAsync(source.ChannelId, batch, ct);
            }

            if (batch.Count > 0)
                totalInserted += await FlushBatchAsync(source.ChannelId, batch, ct);

            if (totalInserted > 0)
            {
                logger.LogInformation(
                    "Channel {ChannelId} ({Name}): {Count} new messages captured",
                    source.ChannelId, source.Name, totalInserted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await syncStateRepo.RecordErrorAsync(source.ChannelId, ex.Message, CancellationToken.None);
            logger.LogError(ex,
                "Failed syncing channel {ChannelId} ({Name}); error recorded in sync_state",
                source.ChannelId, source.Name);
        }

        return totalInserted;
    }

    private async Task<int> FlushBatchAsync(long channelId, List<RawMessageEntity> batch, CancellationToken ct)
    {
        var inserted = await messageRepo.InsertAsync(batch, ct);
        var maxId = 0L;
        foreach (var m in batch)
            if (m.MessageId > maxId) maxId = m.MessageId;

        await syncStateRepo.UpsertAsync(new RawSyncStateEntity
        {
            ChannelId = channelId,
            LastMessageId = maxId,
            LastSyncedAt = DateTimeOffset.UtcNow,
            LastError = null,
            ConsecutiveErrors = 0,
        }, ct);

        batch.Clear();
        return inserted;
    }

    // -------------------------------------------------------------------------
    // Pin polling — snapshot-on-change on the set of pinned IDs, plus edit
    // capture for any pinned message whose `edited_timestamp` we haven't seen.
    // This is the only current path populating raw_message_edits; broader edit
    // visibility would require a gateway WebSocket subsystem.
    // -------------------------------------------------------------------------

    private async Task SyncChannelPinsAsync(DiscordChannelRaw source, long guildId, CancellationToken ct)
    {
        DiscordChannelPins pins;
        try
        {
            pins = await discord.GetChannelPinsAsync(source.ChannelId.ToString(), guildId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            logger.LogDebug(
                "Channel {ChannelId} ({Name}): pins forbidden, skipping",
                source.ChannelId, source.Name);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Channel {ChannelId} ({Name}): pin fetch failed, will retry next pass",
                source.ChannelId, source.Name);
            return;
        }

        await SnapshotPinsIfChangedAsync(source, pins.PayloadJson, ct);

        if (pins.Messages.Count == 0) return;

        // Any pinned message we haven't captured yet goes into raw_messages so
        // projection + enrichment can pick it up on their next pass. Existing
        // rows are left untouched (Tier 1 stays immutable).
        var rawMessages = new List<RawMessageEntity>(pins.Messages.Count);
        var now = DateTimeOffset.UtcNow;
        foreach (var m in pins.Messages)
        {
            rawMessages.Add(new RawMessageEntity
            {
                MessageId = m.MessageId,
                ChannelId = m.ChannelId,
                GuildId = m.GuildId,
                CreatedAt = m.CreatedAt,
                FetchedAt = now,
                Payload = m.Payload,
            });
        }
        await messageRepo.InsertAsync(rawMessages, ct);

        // Edit capture: append a raw_message_edits row for every pinned message
        // that has an edited_timestamp. ON CONFLICT on (message_id, edited_at)
        // makes this idempotent across polls — repeated observation of the
        // same edit produces no new rows.
        var edits = new List<RawMessageEditEntity>();
        foreach (var m in pins.Messages)
        {
            if (m.EditedAt is null) continue;
            edits.Add(new RawMessageEditEntity
            {
                MessageId = m.MessageId,
                EditedAt = m.EditedAt.Value,
                FetchedAt = now,
                Payload = m.Payload,
            });
        }

        if (edits.Count > 0)
        {
            var inserted = await editRepo.InsertIfNewAsync(edits, ct);
            if (inserted > 0)
            {
                logger.LogInformation(
                    "Channel {ChannelId} ({Name}): captured {Count} new pinned-message edits",
                    source.ChannelId, source.Name, inserted);
            }
        }
    }

    private async Task SnapshotPinsIfChangedAsync(DiscordChannelRaw source, string pinsPayloadJson, CancellationToken ct)
    {
        var existing = await pinRepo.GetCurrentPayloadAsync(source.ChannelId, ct);
        if (existing is not null &&
            PayloadCanonicalization.PinSetCanonical(existing) ==
            PayloadCanonicalization.PinSetCanonical(pinsPayloadJson))
        {
            return;
        }

        await pinRepo.InsertSnapshotAsync(new RawPinEntity
        {
            ChannelId = source.ChannelId,
            FetchedAt = DateTimeOffset.UtcNow,
            Payload = pinsPayloadJson,
        }, ct);

        logger.LogInformation(
            "Channel {ChannelId} ({Name}) pin set snapshot written ({Reason})",
            source.ChannelId, source.Name,
            existing is null ? "first sighting" : "pin set drifted");
    }
}
