using DiscordScraper.Core.Queries;

namespace DiscordScraper.Api.Endpoints;

public static class ChannelEndpoints
{
    public static void MapTo(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/guilds/{guildId:long}/channels/active", HandleGetActiveChannels)
            .Produces<IReadOnlyList<ChannelSummary>>(StatusCodes.Status200OK)
            .WithName("GetActiveChannels");

        app.MapGet("/api/channels/{channelId:long}/messages/recent", HandleGetRecentMessages)
            .Produces<IReadOnlyList<RenderedMessage>>(StatusCodes.Status200OK)
            .WithName("GetRecentMessages");
    }

    private static async Task<IResult> HandleGetActiveChannels(
        long guildId,
        IChannelQueryService queryService,
        int sinceHours = 24,
        CancellationToken ct = default)
    {
        var channels = await queryService.GetActiveChannelsAsync(guildId, sinceHours, ct);
        return Results.Ok(channels);
    }

    private static async Task<IResult> HandleGetRecentMessages(
        long channelId,
        IChannelQueryService queryService,
        int count = 50,
        RenderFormat format = RenderFormat.Markdown,
        CancellationToken ct = default)
    {
        var messages = await queryService.GetRecentMessagesAsync(channelId, count, format, ct);
        return Results.Ok(messages);
    }
}
