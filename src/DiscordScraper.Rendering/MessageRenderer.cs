using System.Net;
using System.Text;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Core.Queries;

namespace DiscordScraper.Rendering;

/// <summary>
/// Pure function renderer. Walks a MessageIR against a RenderContext hydrated from read-side
/// entity tables (channel/role/user/emoji name dictionaries). No IO, no DI.
/// </summary>
public static class MessageRenderer
{
    public static string RenderToPlainText(MessageIR ir, RenderContext ctx) =>
        Render(ir, RenderFormat.PlainText, ctx);

    public static string RenderToMarkdown(MessageIR ir, RenderContext ctx) =>
        Render(ir, RenderFormat.Markdown, ctx);

    public static string RenderToHtml(MessageIR ir, RenderContext ctx) =>
        Render(ir, RenderFormat.Html, ctx);

    public static string Render(MessageIR ir, RenderFormat format, RenderContext ctx)
    {
        var sb = new StringBuilder();

        // Reply context prefix — meaningful in all formats
        if (ir.ReplyTo is not null)
            AppendReplyContext(ir.ReplyTo, format, ctx, sb);

        AppendNodes(ir.Body, format, ctx, sb);
        AppendTrailer(ir, format, ctx, sb);

        return sb.ToString().Trim();
    }

    // -------------------------------------------------------------------------
    // Node dispatch
    // -------------------------------------------------------------------------

    private static void AppendNodes(IReadOnlyList<MessageNode> nodes, RenderFormat fmt,
        RenderContext ctx, StringBuilder sb)
    {
        foreach (var node in nodes)
            AppendNode(node, fmt, ctx, sb);
    }

    private static void AppendNode(MessageNode node, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        switch (node)
        {
            case TextNode t:
                AppendText(t.Text, fmt, sb);
                break;

            case MentionNode m:
                AppendMention(m, fmt, ctx, sb);
                break;

            case ChannelRefNode c:
                AppendChannelRef(c, fmt, ctx, sb);
                break;

            case EmojiNode e:
                AppendEmoji(e, fmt, ctx, sb);
                break;

            case TimestampNode ts:
                AppendTimestamp(ts, fmt, sb);
                break;

            case FormattingNode f:
                AppendFormatting(f, fmt, ctx, sb);
                break;

            case CodeBlockNode cb:
                AppendCodeBlock(cb, fmt, sb);
                break;

            case InlineCodeNode ic:
                AppendInlineCode(ic, fmt, sb);
                break;

            case QuoteNode q:
                AppendQuote(q, fmt, ctx, sb);
                break;

            case LinkNode l:
                AppendLink(l, fmt, ctx, sb);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Per-node per-format rendering
    // -------------------------------------------------------------------------

    private static void AppendText(string text, RenderFormat fmt, StringBuilder sb)
    {
        if (fmt == RenderFormat.Html)
            sb.Append(HtmlEncode(text));
        else
            sb.Append(text);
    }

    private static void AppendMention(MentionNode m, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        switch (m.Kind)
        {
            case MentionKind.Everyone:
                if (fmt == RenderFormat.Html)
                    sb.Append("<span class=\"mention mention-everyone\">@everyone</span>");
                else
                    sb.Append("@everyone");
                return;

            case MentionKind.Here:
                if (fmt == RenderFormat.Html)
                    sb.Append("<span class=\"mention mention-here\">@here</span>");
                else
                    sb.Append("@here");
                return;

            case MentionKind.Role:
            {
                var name = (m.Id.HasValue && ctx.RoleNames.TryGetValue(m.Id.Value, out var rn))
                    ? rn
                    : m.Fallback ?? m.Id?.ToString() ?? "unknown";

                if (fmt == RenderFormat.Html)
                    sb.Append($"<span class=\"mention mention-role\">@{HtmlEncode(name)}</span>");
                else
                {
                    sb.Append('@');
                    sb.Append(name);
                }
                return;
            }

            case MentionKind.User:
            default:
            {
                var name = (m.Id.HasValue && ctx.UserNames.TryGetValue(m.Id.Value, out var un))
                    ? un
                    : m.Fallback ?? m.Id?.ToString() ?? "unknown";

                if (fmt == RenderFormat.Html)
                    sb.Append($"<span class=\"mention mention-user\">@{HtmlEncode(name)}</span>");
                else
                {
                    sb.Append('@');
                    sb.Append(name);
                }
                return;
            }
        }
    }

    private static void AppendChannelRef(ChannelRefNode c, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        var name = ctx.ChannelNames.TryGetValue(c.ChannelId, out var cn)
            ? cn
            : c.Fallback ?? c.ChannelId.ToString();

        if (fmt == RenderFormat.Html)
            sb.Append($"<span class=\"mention mention-channel\">#{HtmlEncode(name)}</span>");
        else
        {
            sb.Append('#');
            sb.Append(name);
        }
    }

    private static void AppendEmoji(EmojiNode e, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        if (e.Kind == EmojiKind.Unicode)
        {
            // Unicode glyphs render identically across all formats; HTML lets CSS handle emoji styling.
            sb.Append(e.NameOrGlyph);
            return;
        }

        // Custom emoji: prefer name from context, then node's own name
        var name = (e.Id.HasValue && ctx.EmojiNames.TryGetValue(e.Id.Value, out var en))
            ? en
            : e.NameOrGlyph;

        if (fmt == RenderFormat.Html && e.Id.HasValue)
        {
            var ext = e.Animated ? "gif" : "png";
            var altText = HtmlEncode($":{name}:");
            sb.Append($"<img class=\"emoji\" src=\"https://cdn.discordapp.com/emojis/{e.Id.Value}.{ext}\" alt=\"{altText}\">");
        }
        else
        {
            sb.Append(':');
            sb.Append(name);
            sb.Append(':');
        }
    }

    private static void AppendTimestamp(TimestampNode ts, RenderFormat fmt, StringBuilder sb)
    {
        // Plain text and markdown: ISO-8601 date is the most portable representation.
        // HTML: wrap in <time> for semantic richness and potential client-side formatting.
        var iso = ts.Value.ToString("o");

        if (fmt == RenderFormat.Html)
        {
            // Human-readable label matches the Discord timestamp style for the given format.
            var label = ts.Style switch
            {
                TimestampStyle.ShortTime     => ts.Value.ToString("HH:mm"),
                TimestampStyle.LongTime      => ts.Value.ToString("HH:mm:ss"),
                TimestampStyle.ShortDate     => ts.Value.ToString("dd/MM/yyyy"),
                TimestampStyle.LongDate      => ts.Value.ToString("d MMMM yyyy"),
                TimestampStyle.ShortDateTime => ts.Value.ToString("d MMM yyyy HH:mm"),
                TimestampStyle.LongDateTime  => ts.Value.ToString("dddd, d MMMM yyyy HH:mm"),
                TimestampStyle.Relative      => iso,
                _                            => iso
            };
            sb.Append($"<time datetime=\"{iso}\">{HtmlEncode(label)}</time>");
        }
        else
        {
            sb.Append(ts.Value.ToString("yyyy-MM-dd"));
        }
    }

    private static void AppendFormatting(FormattingNode f, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        if (fmt == RenderFormat.PlainText)
        {
            // Drop markers entirely — plain text is marker-free.
            AppendNodes(f.Children, fmt, ctx, sb);
            return;
        }

        if (fmt == RenderFormat.Html)
        {
            var (open, close) = f.Kind switch
            {
                FormattingKind.Bold          => ("<strong>", "</strong>"),
                FormattingKind.Italic        => ("<em>", "</em>"),
                FormattingKind.Underline     => ("<u>", "</u>"),
                FormattingKind.Strikethrough => ("<s>", "</s>"),
                FormattingKind.Spoiler       => ("<span class=\"spoiler\">", "</span>"),
                _                            => ("", "")
            };
            sb.Append(open);
            AppendNodes(f.Children, fmt, ctx, sb);
            sb.Append(close);
            return;
        }

        // Markdown: reapply Discord markers
        var (mdOpen, mdClose) = f.Kind switch
        {
            FormattingKind.Bold          => ("**", "**"),
            FormattingKind.Italic        => ("*", "*"),
            FormattingKind.Underline     => ("__", "__"),
            FormattingKind.Strikethrough => ("~~", "~~"),
            FormattingKind.Spoiler       => ("||", "||"),
            _                            => ("", "")
        };
        sb.Append(mdOpen);
        AppendNodes(f.Children, fmt, ctx, sb);
        sb.Append(mdClose);
    }

    private static void AppendCodeBlock(CodeBlockNode cb, RenderFormat fmt, StringBuilder sb)
    {
        switch (fmt)
        {
            case RenderFormat.PlainText:
                sb.Append('\n');
                sb.Append(cb.Content);
                sb.Append('\n');
                break;

            case RenderFormat.Markdown:
                sb.Append("```");
                if (!string.IsNullOrEmpty(cb.Language))
                {
                    sb.Append(cb.Language);
                }
                sb.Append('\n');
                sb.Append(cb.Content);
                sb.Append("\n```");
                break;

            case RenderFormat.Html:
                var langClass = string.IsNullOrEmpty(cb.Language) ? "" : $" class=\"language-{HtmlEncode(cb.Language)}\"";
                sb.Append($"<pre><code{langClass}>{HtmlEncode(cb.Content)}</code></pre>");
                break;
        }
    }

    private static void AppendInlineCode(InlineCodeNode ic, RenderFormat fmt, StringBuilder sb)
    {
        switch (fmt)
        {
            case RenderFormat.PlainText:
                sb.Append(ic.Content);
                break;
            case RenderFormat.Markdown:
                sb.Append('`');
                sb.Append(ic.Content);
                sb.Append('`');
                break;
            case RenderFormat.Html:
                sb.Append("<code>");
                sb.Append(HtmlEncode(ic.Content));
                sb.Append("</code>");
                break;
        }
    }

    private static void AppendQuote(QuoteNode q, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        if (fmt == RenderFormat.Html)
        {
            sb.Append("<blockquote>");
            AppendNodes(q.Children, fmt, ctx, sb);
            sb.Append("</blockquote>");
            return;
        }

        // For plain text and markdown the "> " prefix applies per-line.
        // Capture children into a temporary buffer, then prefix each line.
        var inner = new StringBuilder();
        AppendNodes(q.Children, fmt, ctx, inner);
        var lines = inner.ToString().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append("> ");
            sb.Append(lines[i]);
        }
    }

    private static void AppendLink(LinkNode l, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        var displayBuffer = new StringBuilder();
        AppendNodes(l.DisplayChildren, fmt, ctx, displayBuffer);
        var display = displayBuffer.ToString();
        var hasDisplay = display.Length > 0 && display != l.Url;

        switch (fmt)
        {
            case RenderFormat.PlainText:
                if (hasDisplay)
                {
                    sb.Append(display);
                    sb.Append(" (");
                    sb.Append(l.Url);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(l.Url);
                }
                break;

            case RenderFormat.Markdown:
                if (hasDisplay)
                {
                    sb.Append('[');
                    sb.Append(display);
                    sb.Append("](");
                    sb.Append(l.Url);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(l.Url);
                }
                break;

            case RenderFormat.Html:
                sb.Append($"<a href=\"{HtmlEncode(l.Url)}\">");
                if (hasDisplay)
                    sb.Append(display); // already HTML-encoded by recursive call
                else
                    sb.Append(HtmlEncode(l.Url));
                sb.Append("</a>");
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Reply context + trailer (attachments/embeds)
    // -------------------------------------------------------------------------

    private static void AppendReplyContext(ReplyContext reply, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        switch (fmt)
        {
            case RenderFormat.PlainText:
                // Skip in plain-text mode — reply context adds noise to embedding input.
                break;

            case RenderFormat.Markdown:
            {
                sb.Append("> ↪ replying to ");
                if (reply.ReplyToAuthorId.HasValue
                    && ctx.UserNames.TryGetValue(reply.ReplyToAuthorId.Value, out var name))
                    sb.Append('@').Append(name);
                else if (reply.ReplyToAuthorId.HasValue)
                    sb.Append('@').Append(reply.ReplyToAuthorId.Value);
                else
                    sb.Append("@unknown");
                sb.Append('\n');
                break;
            }

            case RenderFormat.Html:
            {
                var name = reply.ReplyToAuthorId.HasValue
                    && ctx.UserNames.TryGetValue(reply.ReplyToAuthorId.Value, out var un)
                    ? un
                    : reply.ReplyToAuthorId?.ToString() ?? "unknown";
                sb.Append($"<div class=\"reply-context\">↪ replying to <span class=\"mention mention-user\">@{HtmlEncode(name)}</span></div>\n");
                break;
            }
        }
    }

    private static void AppendTrailer(MessageIR ir, RenderFormat fmt, RenderContext ctx, StringBuilder sb)
    {
        if (fmt == RenderFormat.PlainText)
            return; // embedding input stays clean — no footer noise

        if (ir.Attachments.Count > 0)
            AppendAttachments(ir.Attachments, fmt, sb);

        if (ir.Embeds.Count > 0)
            AppendEmbeds(ir.Embeds, fmt, sb);
    }

    private static void AppendAttachments(IReadOnlyList<AttachmentIR> attachments, RenderFormat fmt, StringBuilder sb)
    {
        if (fmt == RenderFormat.Markdown)
        {
            sb.Append("\n\n---\n");
            foreach (var a in attachments)
            {
                var label = a.Filename ?? a.Url;
                sb.Append($"[{label}]({a.Url})");
                sb.Append('\n');
            }
            return;
        }

        // HTML: inline images for image/* content types, links for everything else
        sb.Append("\n<div class=\"attachments\">");
        foreach (var a in attachments)
        {
            if (a.ContentType is not null && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var alt = HtmlEncode(a.Filename ?? "attachment");
                sb.Append($"<img class=\"attachment-image\" src=\"{HtmlEncode(a.Url)}\" alt=\"{alt}\">");
            }
            else
            {
                var label = HtmlEncode(a.Filename ?? a.Url);
                sb.Append($"<a class=\"attachment-file\" href=\"{HtmlEncode(a.Url)}\">{label}</a>");
            }
        }
        sb.Append("</div>");
    }

    private static void AppendEmbeds(IReadOnlyList<EmbedIR> embeds, RenderFormat fmt, StringBuilder sb)
    {
        if (fmt == RenderFormat.Markdown)
        {
            sb.Append("\n\n---\n");
            foreach (var e in embeds)
            {
                if (!string.IsNullOrEmpty(e.Title))
                    sb.Append($"**{e.Title}**\n");
                if (!string.IsNullOrEmpty(e.Description))
                    sb.Append(e.Description).Append('\n');
                if (!string.IsNullOrEmpty(e.Url))
                    sb.Append(e.Url).Append('\n');
            }
            return;
        }

        // HTML: card-style embed blocks
        sb.Append("\n<div class=\"embeds\">");
        foreach (var e in embeds)
        {
            sb.Append("<div class=\"embed-card\">");
            if (!string.IsNullOrEmpty(e.Title))
                sb.Append($"<div class=\"embed-title\">{HtmlEncode(e.Title)}</div>");
            if (!string.IsNullOrEmpty(e.Description))
                sb.Append($"<div class=\"embed-description\">{HtmlEncode(e.Description)}</div>");
            if (!string.IsNullOrEmpty(e.Url))
                sb.Append($"<div class=\"embed-url\"><a href=\"{HtmlEncode(e.Url!)}\">{HtmlEncode(e.Url)}</a></div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");
    }

    // -------------------------------------------------------------------------
    // HTML safety
    // -------------------------------------------------------------------------

    // WebUtility.HtmlEncode handles &, <, >, ", ' — sufficient to prevent XSS in attribute
    // values and text content. All user-controlled strings (text nodes, names, filenames,
    // URLs used as attribute values) must pass through this before landing in HTML output.
    private static string HtmlEncode(string value) => WebUtility.HtmlEncode(value);
}
