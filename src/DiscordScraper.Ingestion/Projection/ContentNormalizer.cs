using System.Text.RegularExpressions;

namespace DiscordScraper.Ingestion.Projection;

/// <summary>
/// Stateless text transforms applied to a message's <c>content</c> during
/// projection. Resolves Discord mention tokens to human-readable form and
/// strips zero-width noise that slips in via phone keyboards, copy/paste from
/// websites, and emoji-style formatters.
/// </summary>
internal static partial class ContentNormalizer
{
    // Role pattern must run before user pattern: <@&id> starts with <@ so the
    // user regex would claim it otherwise. Patterns are compiled once at class
    // load via the partial-method source generator.
    [GeneratedRegex(@"<@&(\d+)>", RegexOptions.Compiled)]
    private static partial Regex RoleMentionRegex();

    [GeneratedRegex(@"<@!?(\d+)>", RegexOptions.Compiled)]
    private static partial Regex UserMentionRegex();

    [GeneratedRegex(@"<#(\d+)>", RegexOptions.Compiled)]
    private static partial Regex ChannelMentionRegex();

    // Custom emoji: <:name:123456> or <a:name:123456> for animated. The image
    // URL is intentionally not materialized — downstream summarization can
    // handle the `:name:` form directly.
    [GeneratedRegex(@"<a?:([A-Za-z0-9_~-]+):\d+>", RegexOptions.Compiled)]
    private static partial Regex CustomEmojiRegex();

    // ZWSP, ZWNJ, ZWJ, word joiner, BOM. These land in Discord via phone
    // keyboards and some websites and never add meaning.
    [GeneratedRegex("[\\u200B-\\u200D\\u2060\\uFEFF]", RegexOptions.Compiled)]
    private static partial Regex ZeroWidthRegex();

    public static string Normalize(
        string content,
        IReadOnlyDictionary<long, string> messageUserMentions,
        IReadOnlyDictionary<long, string> guildRoles,
        IReadOnlyDictionary<long, ChannelInfo> channels)
    {
        if (string.IsNullOrEmpty(content)) return content;

        // Roles first so <@&X> isn't consumed by the user regex.
        content = RoleMentionRegex().Replace(content, match =>
        {
            var id = long.Parse(match.Groups[1].Value);
            return guildRoles.TryGetValue(id, out var name) ? $"@{name}" : $"@role_{id}";
        });

        content = UserMentionRegex().Replace(content, match =>
        {
            var id = long.Parse(match.Groups[1].Value);
            return messageUserMentions.TryGetValue(id, out var name) ? $"@{name}" : $"@user_{id}";
        });

        content = ChannelMentionRegex().Replace(content, match =>
        {
            var id = long.Parse(match.Groups[1].Value);
            return channels.TryGetValue(id, out var info) ? $"#{info.Name}" : $"#channel_{id}";
        });

        content = CustomEmojiRegex().Replace(content, match => $":{match.Groups[1].Value}:");

        content = ZeroWidthRegex().Replace(content, string.Empty);

        return content;
    }
}
