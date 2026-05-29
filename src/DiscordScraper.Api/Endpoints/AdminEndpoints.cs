using DiscordScraper.Api.Admin;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Contracts.Events.Message;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace DiscordScraper.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapTo(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/stats", HandleStats)
            .Produces<AdminStatsResponse>(StatusCodes.Status200OK);

        group.MapGet("/sync/status", HandleSyncStatus)
            .Produces<SyncStatusResponse>(StatusCodes.Status200OK);

        group.MapGet("/sync/guilds", HandleListGuilds)
            .Produces<IReadOnlyList<GuildSagaSnapshot>>(StatusCodes.Status200OK);

        group.MapGet("/sync/channels", HandleListChannels)
            .Produces<IReadOnlyList<ChannelSagaSnapshot>>(StatusCodes.Status200OK);

        group.MapPost("/sync/guilds/{guildId:long}", HandleForceGuildSync)
            .Produces(StatusCodes.Status202Accepted);

        group.MapPost("/sync/channels/{channelId:long}", HandleForceChannelSync)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/sagas/messages/{messageSnowflake:long}", HandleGetMessageSaga)
            .Produces<MessageSagaSnapshot>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/sagas/messages/replay-faulted", HandleReplayFaulted)
            .Produces(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> HandleStats(
        ISagaIntrospection sagas,
        IReadStoreStatistics readStore,
        TimeProvider clock,
        CancellationToken ct)
    {
        var sagaTask  = sagas.GetCountsAsync(ct);
        var storeTask = readStore.GetCountsAsync(ct);

        await Task.WhenAll(sagaTask, storeTask);

        return Results.Ok(new AdminStatsResponse(
            ReadStore:   await storeTask,
            Sagas:       await sagaTask,
            GeneratedAt: clock.GetUtcNow()));
    }

    private static async Task<IResult> HandleSyncStatus(
        ISagaIntrospection sagas,
        TimeProvider clock,
        CancellationToken ct)
    {
        var counts = await sagas.GetCountsAsync(ct);

        // "Recently synced" = channel sagas currently in the Syncing state.
        counts.Channel.TryGetValue("Syncing", out var recentlySyncing);

        return Results.Ok(new SyncStatusResponse(
            Counts:               counts,
            RecentlySyncedChannels: (int)recentlySyncing,
            GeneratedAt:          clock.GetUtcNow()));
    }

    private static async Task<IResult> HandleListGuilds(
        ISagaIntrospection sagas,
        CancellationToken ct)
    {
        var guilds = await sagas.ListGuildSagasAsync(ct);
        return Results.Ok(guilds);
    }

    private static async Task<IResult> HandleListChannels(
        ISagaIntrospection sagas,
        [FromQuery] long? guildId,
        CancellationToken ct)
    {
        var channels = await sagas.ListChannelSagasAsync(guildId, ct);
        return Results.Ok(channels);
    }

    private static async Task<IResult> HandleForceGuildSync(
        long guildId,
        IPublishEndpoint publish,
        TimeProvider clock,
        CancellationToken ct)
    {
        await publish.Publish<GuildSyncDue>(new
        {
            GuildId      = guildId,
            CurrentState = "Syncing",
            UpdatedOn = clock.GetUtcNow(),
        }, ct);

        return Results.Accepted();
    }

    private static async Task<IResult> HandleForceChannelSync(
        long channelId,
        [FromQuery] long? guildId,
        [FromQuery] long? cursorSnowflake,
        IPublishEndpoint publish,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (guildId is null)
            return Results.BadRequest("guildId query parameter required");

        await publish.Publish<ChannelSyncDue>(new
        {
            ChannelId       = channelId,
            GuildId         = guildId.Value,
            CursorSnowflake = (long?)cursorSnowflake,
            CurrentState    = "Syncing",
            UpdatedOn   = clock.GetUtcNow(),
        }, ct);

        return Results.Accepted();
    }

    private static async Task<IResult> HandleGetMessageSaga(
        long messageSnowflake,
        ISagaIntrospection sagas,
        CancellationToken ct)
    {
        var snapshot = await sagas.GetMessageSagaAsync(messageSnowflake, ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleReplayFaulted(
        [FromQuery] string? phase,
        IPublishEndpoint publish,
        TimeProvider clock,
        CancellationToken ct)
    {
        await publish.Publish<MessageReplayRequested>(new
        {
            Timestamp = clock.GetUtcNow(),
            Phase = phase,
        }, ct);

        return Results.Accepted();
    }
}
