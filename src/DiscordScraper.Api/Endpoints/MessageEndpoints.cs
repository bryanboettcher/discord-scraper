using DiscordScraper.Api.Endpoints.Models;
using DiscordScraper.Core.Queries;

namespace DiscordScraper.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapTo(IEndpointRouteBuilder app)
    {
        // No auth for v1 — Api is local-only; MCP runs on the same host.
        // Add bearer/API-key middleware here when the surface is exposed externally.

        app.MapPost("/api/messages/search", HandleSearch)
            .Produces<IReadOnlyList<ScoredMessage>>(StatusCodes.Status200OK)
            .WithName("SearchMessages");

        app.MapGet("/api/messages/{id:long}", HandleGetMessage)
            .Produces<RenderedMessage>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetMessage");

        app.MapGet("/api/messages/{id:long}/context", HandleGetContext)
            .Produces<RenderedConversation>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithName("GetMessageContext");
    }

    private static async Task<IResult> HandleSearch(
        MessageSearchRequest request,
        IMessageQueryService queryService,
        CancellationToken ct)
    {
        var results = await queryService.SearchAsync(request.ToQuery(), ct);
        return Results.Ok(results);
    }

    private static async Task<IResult> HandleGetMessage(
        long id,
        IMessageQueryService queryService,
        RenderFormat format = RenderFormat.Markdown,
        CancellationToken ct = default)
    {
        var message = await queryService.GetMessageAsync(id, format, ct);
        return message is null ? Results.NotFound() : Results.Ok(message);
    }

    private static async Task<IResult> HandleGetContext(
        long id,
        IMessageQueryService queryService,
        int radius = 3,
        RenderFormat format = RenderFormat.Markdown,
        CancellationToken ct = default)
    {
        var conversation = await queryService.GetConversationContextAsync(id, radius, format, ct);

        // Center is null when the message was not found in read_messages.
        return conversation.Center is null ? Results.NotFound() : Results.Ok(conversation);
    }
}
