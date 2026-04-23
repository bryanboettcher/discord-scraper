using System.Globalization;
using System.Text.Json;
using DiscordScraper.Storage.Entities;

namespace DiscordScraper.Ingestion.Projection;

/// <summary>
/// Pure transform from a Tier 1 raw message (opaque JSONB) into the Tier 2
/// projection shape. Stateless — all contextual data (channels, roles) flows
/// in via <see cref="ProjectionContext"/>. Re-running this function over the
/// same inputs must produce an identical <see cref="MessageEntity"/> so the
/// idempotent upsert semantic holds.
/// </summary>
internal static class MessageProjector
{
    public static MessageEntity Project(RawMessageEntity raw, ProjectionContext context)
    {
        using var doc = JsonDocument.Parse(raw.Payload);
        var root = doc.RootElement;

        var author = root.GetProperty("author");
        var authorId = long.Parse(author.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        var authorName = author.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String
            ? gn.GetString()!
            : author.GetProperty("username").GetString() ?? "";
        var authorIsBot = author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True;

        // User mentions embedded in the message payload are authoritative for
        // what Discord itself could resolve at send time. Deleted users drop
        // out of this array and fall back to @user_<id> in the content.
        var userMentions = new Dictionary<long, string>();
        if (root.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mentions.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = long.Parse(idEl.GetString()!, CultureInfo.InvariantCulture);
                var name = m.TryGetProperty("global_name", out var gnM) && gnM.ValueKind == JsonValueKind.String
                    ? gnM.GetString()!
                    : m.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";
                userMentions[id] = name;
            }
        }

        var guildRoles = context.GuildRoles.TryGetValue(raw.GuildId, out var rolesForGuild)
            ? rolesForGuild
            : EmptyRoles;

        var rawContent = root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? ""
            : "";
        var content = ContentNormalizer.Normalize(rawContent, userMentions, guildRoles, context.Channels);

        long? replyToId = null;
        if (root.TryGetProperty("message_reference", out var mref) && mref.ValueKind == JsonValueKind.Object &&
            mref.TryGetProperty("message_id", out var refId) && refId.ValueKind == JsonValueKind.String)
        {
            replyToId = long.Parse(refId.GetString()!, CultureInfo.InvariantCulture);
        }

        // Thread detection: a message lives on a thread (types 10/11/12) when
        // its channel is one of those types; root_channel_id walks up to the
        // parent so channel-scoped views include both root messages and their
        // threaded replies.
        long? threadId = null;
        var rootChannelId = raw.ChannelId;
        if (context.Channels.TryGetValue(raw.ChannelId, out var channelInfo) &&
            channelInfo.Type is 10 or 11 or 12)
        {
            threadId = raw.ChannelId;
            if (channelInfo.ParentId is long parent)
                rootChannelId = parent;
        }

        var hasAttachments = root.TryGetProperty("attachments", out var atts)
            && atts.ValueKind == JsonValueKind.Array
            && atts.GetArrayLength() > 0;

        DateTimeOffset? editedAt = null;
        if (root.TryGetProperty("edited_timestamp", out var ed) && ed.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ed.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                editedAt = parsed;
            }
        }

        return new MessageEntity
        {
            MessageId = raw.MessageId,
            ChannelId = raw.ChannelId,
            GuildId = raw.GuildId,
            AuthorId = authorId,
            AuthorName = authorName,
            AuthorIsBot = authorIsBot,
            Content = content,
            CreatedAt = raw.CreatedAt,
            EditedAt = editedAt,
            ReplyToId = replyToId,
            ThreadId = threadId,
            RootChannelId = rootChannelId,
            HasAttachments = hasAttachments,
        };
    }

    private static readonly IReadOnlyDictionary<long, string> EmptyRoles =
        new Dictionary<long, string>();
}
