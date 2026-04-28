namespace DiscordScraper.Rendering;

public sealed record RenderContext(
    IReadOnlyDictionary<long, string> ChannelNames,
    IReadOnlyDictionary<long, string> RoleNames,
    IReadOnlyDictionary<long, string> UserNames,
    IReadOnlyDictionary<long, string> EmojiNames)
{
    public static readonly RenderContext Empty = new(
        new Dictionary<long, string>(),
        new Dictionary<long, string>(),
        new Dictionary<long, string>(),
        new Dictionary<long, string>());
}
