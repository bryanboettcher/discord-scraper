using System.Globalization;
using System.Text.Json;
using DiscordScraper.Contracts.IR;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Parsing;

/// <summary>
/// Parses a raw Discord message payload into a typed IR.
///
/// Responsibilities:
///   1. Extract structural fields (author, mentions[], attachments[], embeds[],
///      referenced_message) from the JSON payload.
///   2. Build the user-mention fallback dictionary from mentions[].
///   3. Delegate content string parsing to <see cref="ContentScanner"/>.
///   4. Return a fully-formed <see cref="MessageIR"/>.
///
/// This class is a pure transform — no I/O, no side effects. Thread-safe; a single
/// instance can be registered as a singleton.
/// </summary>
internal sealed class MessageParser(ILogger<MessageParser> logger) : IMessageParser
{
    public MessageIR Parse(string payloadJson, ParseContext context)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            logger.LogWarning("MessageParser received empty payloadJson; returning empty IR");
            return EmptyIR(context.CapturedAt);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return ParseDocument(doc.RootElement, context);
        }
        catch (JsonException ex)
        {
            // Malformed payload: return empty IR so the saga doesn't fault hard.
            // The raw payload is preserved on the saga for re-projection once data quality is fixed.
            logger.LogWarning(ex, "MessageParser failed to parse JSON payload; returning empty IR");
            return EmptyIR(context.CapturedAt);
        }
    }

    private MessageIR ParseDocument(JsonElement root, ParseContext context)
    {
        // Build user mention fallback map from payload's mentions[] array.
        // Discord includes full user objects here for every @mention in the message.
        var userMentions = BuildUserMentions(root);

        var scanCtx = new ScanContext
        {
            UserMentions = userMentions,
            HomeChannelId = context.HomeChannelId,
            HomeChannelName = context.HomeChannelName,
        };

        var rawContent = root.TryGetProperty("content", out var contentEl) &&
                         contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? string.Empty
            : string.Empty;

        var body = ContentScanner.Scan(rawContent, scanCtx);
        var attachments = ParseAttachments(root);
        var embeds = ParseEmbeds(root);
        var replyTo = ParseReplyContext(root);

        return new MessageIR(
            Body: body,
            Attachments: attachments,
            Embeds: embeds,
            ReplyTo: replyTo,
            CapturedAt: context.CapturedAt);
    }

    private static IReadOnlyDictionary<long, string> BuildUserMentions(JsonElement root)
    {
        if (!root.TryGetProperty("mentions", out var mentionsEl) ||
            mentionsEl.ValueKind != JsonValueKind.Array ||
            mentionsEl.GetArrayLength() == 0)
        {
            return EmptyMentions;
        }

        var dict = new Dictionary<long, string>();
        foreach (var m in mentionsEl.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;

            if (!long.TryParse(idEl.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                continue;

            // global_name is preferred (display name); fall back to username (handle).
            var name = m.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String
                ? gn.GetString()
                : null;

            name ??= m.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String
                ? un.GetString()
                : null;

            if (name is not null)
                dict[id] = name;
        }

        return dict;
    }

    private List<AttachmentIR> ParseAttachments(JsonElement root)
    {
        if (!root.TryGetProperty("attachments", out var attachEl) ||
            attachEl.ValueKind != JsonValueKind.Array ||
            attachEl.GetArrayLength() == 0)
        {
            return [];
        }

        var result = new List<AttachmentIR>(attachEl.GetArrayLength());
        foreach (var a in attachEl.EnumerateArray())
        {
            try
            {
                result.Add(ParseAttachment(a));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MessageParser skipped unparseable attachment element");
            }
        }

        return result;
    }

    private static AttachmentIR ParseAttachment(JsonElement a)
    {
        var id = long.Parse(
            a.GetProperty("id").GetString()!,
            NumberStyles.None,
            CultureInfo.InvariantCulture);

        var url = a.GetProperty("url").GetString()!;

        var contentType = a.TryGetProperty("content_type", out var ct) && ct.ValueKind == JsonValueKind.String
            ? ct.GetString()
            : null;

        long? size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
            ? sz.GetInt64()
            : null;

        var filename = a.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
            ? fn.GetString()
            : null;

        return new AttachmentIR(id, url, contentType, size, filename);
    }

    private List<EmbedIR> ParseEmbeds(JsonElement root)
    {
        if (!root.TryGetProperty("embeds", out var embedsEl) ||
            embedsEl.ValueKind != JsonValueKind.Array ||
            embedsEl.GetArrayLength() == 0)
        {
            return [];
        }

        var result = new List<EmbedIR>(embedsEl.GetArrayLength());
        var index = 0;
        foreach (var e in embedsEl.EnumerateArray())
        {
            try
            {
                result.Add(ParseEmbed(e, index));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MessageParser skipped unparseable embed at index {Index}", index);
            }
            index++;
        }

        return result;
    }

    private static EmbedIR ParseEmbed(JsonElement e, int index)
    {
        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        var url = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

        var title = e.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
            ? ti.GetString()
            : null;

        var description = e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        return new EmbedIR(index, type, url, title, description);
    }

    private static ReplyContext? ParseReplyContext(JsonElement root)
    {
        // Discord uses "referenced_message" for replies. The field is a full message object
        // or null when the original message was deleted.
        if (!root.TryGetProperty("referenced_message", out var refMsg) ||
            refMsg.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long? messageId = refMsg.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String
            ? ParseLongOrNull(mid.GetString())
            : null;

        long? channelId = refMsg.TryGetProperty("channel_id", out var cid) && cid.ValueKind == JsonValueKind.String
            ? ParseLongOrNull(cid.GetString())
            : null;

        long? authorId = null;
        if (refMsg.TryGetProperty("author", out var auth) && auth.ValueKind == JsonValueKind.Object &&
            auth.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.String)
        {
            authorId = ParseLongOrNull(aid.GetString());
        }

        // Only produce a ReplyContext when at least the message ID resolved.
        return messageId.HasValue
            ? new ReplyContext(messageId, channelId, authorId)
            : null;
    }

    private static long? ParseLongOrNull(string? s) =>
        s is not null && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private static MessageIR EmptyIR(DateTimeOffset capturedAt) =>
        new(Body: [], Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: capturedAt);

    private static readonly IReadOnlyDictionary<long, string> EmptyMentions =
        new Dictionary<long, string>();
}
