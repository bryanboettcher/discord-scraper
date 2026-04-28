namespace DiscordScraper.Api.Endpoints;

public static class WebApplicationExtensions
{
    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        MessageEndpoints.MapTo(app);
        ChannelEndpoints.MapTo(app);
        AdminEndpoints.MapTo(app);
        return app;
    }
}
