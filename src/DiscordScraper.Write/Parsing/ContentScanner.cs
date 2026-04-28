using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Write.Parsing;

/// <summary>
/// Linear scanner that walks a Discord markdown string and emits typed IR nodes.
///
/// Strategy: single left-to-right pass. At each position, try patterns in priority order:
///   1. Fenced tokens (code block, inline code) — content inside is opaque; no formatting.
///   2. Discord-flavored tokens (mentions, channel refs, emoji, timestamps) — fixed syntax.
///   3. Multi-line block quote (>>>) — must precede single-line quote check.
///   4. Single-line quote (> at line start).
///   5. Formatting delimiters (bold-italic, bold, italic, underline, strikethrough, spoiler) — recursive.
///   6. Markdown links [display](url).
///   7. Bare URLs (https?://).
///   8. Unicode emoji — accumulated into EmojiNode.
///   9. Fallthrough: accumulate into TextNode.
///
/// Nesting: formatting nodes recurse via <see cref="Scan"/> on the interior span. This handles
/// Discord's actual nesting rules without a full recursive-descent grammar. The recursion depth
/// is bounded by the number of distinct FormattingKind values (5), so stack overflow is not a
/// practical concern for real Discord content.
///
/// Known gaps (documented, not bugs):
/// - Bold-italic-underline triple nesting (e.g., ***__text__***) produces correct nesting
///   for the dominant delimiter but may misattribute the inner kind.
/// - Overlapping (non-nested) formatting spans ("**a *b** c*") fall back to TextNode for
///   the unmatched delimiter rather than attempting DOM-like overlap resolution. Discord's
///   client has its own quirks here; "good enough" is the target.
/// - Zero-width characters (ZWSP etc.) are preserved in TextNode content; stripping them is
///   a display concern handled by the renderer.
/// - Surrogate-pair emoji in strings that are not well-formed UTF-16 produce TextNode
///   fallback rather than EmojiNode. .NET string validation catches these via Rune.
/// </summary>
internal static partial class ContentScanner
{
    // Discord-flavored token patterns — compiled once.
    [GeneratedRegex(@"^<@&(\d+)>")]
    private static partial Regex RoleMentionPattern();

    [GeneratedRegex(@"^<@!?(\d+)>")]
    private static partial Regex UserMentionPattern();

    [GeneratedRegex(@"^<#(\d+)>")]
    private static partial Regex ChannelRefPattern();

    [GeneratedRegex(@"^<(a?):([A-Za-z0-9_~-]+):(\d+)>")]
    private static partial Regex CustomEmojiPattern();

    [GeneratedRegex(@"^<t:(-?\d+)(?::([tTdDfFR]))?>")]
    private static partial Regex TimestampPattern();

    // Markdown link: [display text](url)
    [GeneratedRegex(@"^\[([^\]]*)\]\((https?://[^\)]+)\)")]
    private static partial Regex MarkdownLinkPattern();

    // Bare URL — must not consume trailing punctuation that Discord renders as plain text.
    [GeneratedRegex(@"^(https?://[^\s<>""'\)\]]+)")]
    private static partial Regex BareUrlPattern();

    // Multi-line block quote opener: ">>> " at start of content or after newline.
    // Single-line quote: "> " at start of a line.
    private const string BlockQuotePrefix = ">>> ";
    private const string LineQuotePrefix = "> ";

    public static List<MessageNode> Scan(string text, ScanContext ctx)
    {
        var nodes = new List<MessageNode>();
        if (string.IsNullOrEmpty(text))
            return nodes;

        var sb = new StringBuilder();
        var i = 0;
        var len = text.Length;

        void FlushText()
        {
            if (sb.Length > 0)
            {
                nodes.Add(new TextNode(sb.ToString()));
                sb.Clear();
            }
        }

        while (i < len)
        {
            // ── Code block ─────────────────────────────────────────────────────
            if (i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                FlushText();
                var codeBlock = TryParseCodeBlock(text, i, out var advance);
                if (codeBlock is not null)
                {
                    nodes.Add(codeBlock);
                    i += advance;
                    continue;
                }
            }

            // ── Inline code ────────────────────────────────────────────────────
            if (text[i] == '`')
            {
                FlushText();
                var inlineCode = TryParseInlineCode(text, i, out var advance);
                if (inlineCode is not null)
                {
                    nodes.Add(inlineCode);
                    i += advance;
                    continue;
                }
            }

            // ── Discord-flavored tokens ────────────────────────────────────────
            if (text[i] == '<')
            {
                var slice = text.AsSpan(i);

                // Role before user — <@&id> prefix overlaps <@id>.
                var m = RoleMentionPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    nodes.Add(new MentionNode(MentionKind.Role, id, null));
                    i += m.Length;
                    continue;
                }

                m = UserMentionPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    ctx.UserMentions.TryGetValue(id, out var fallback);
                    nodes.Add(new MentionNode(MentionKind.User, id, fallback));
                    i += m.Length;
                    continue;
                }

                m = ChannelRefPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var channelFallback = ctx.HomeChannelId == id ? ctx.HomeChannelName : null;
                    nodes.Add(new ChannelRefNode(id, channelFallback));
                    i += m.Length;
                    continue;
                }

                m = CustomEmojiPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var animated = m.Groups[1].Value == "a";
                    var name = m.Groups[2].Value;
                    var emojiId = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    nodes.Add(new EmojiNode(EmojiKind.Custom, emojiId, name, animated));
                    i += m.Length;
                    continue;
                }

                m = TimestampPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var unixSeconds = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var styleChar = m.Groups[2].Success ? m.Groups[2].Value : "f";
                    var style = ParseTimestampStyle(styleChar);
                    nodes.Add(new TimestampNode(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), style));
                    i += m.Length;
                    continue;
                }
            }

            // ── @everyone / @here ──────────────────────────────────────────────
            if (text[i] == '@')
            {
                if (text.AsSpan(i).StartsWith("@everyone", StringComparison.Ordinal))
                {
                    FlushText();
                    nodes.Add(new MentionNode(MentionKind.Everyone, null, "@everyone"));
                    i += "@everyone".Length;
                    continue;
                }
                if (text.AsSpan(i).StartsWith("@here", StringComparison.Ordinal))
                {
                    FlushText();
                    nodes.Add(new MentionNode(MentionKind.Here, null, "@here"));
                    i += "@here".Length;
                    continue;
                }
            }

            // ── Multi-line block quote (>>> ) — only valid at position 0 or after \n ──
            var atLineStart = i == 0 || text[i - 1] == '\n';
            if (atLineStart && text.AsSpan(i).StartsWith(BlockQuotePrefix, StringComparison.Ordinal))
            {
                FlushText();
                // Everything from the >>> prefix to end of string is the block quote body.
                var quoteContent = text[(i + BlockQuotePrefix.Length)..];
                var children = Scan(quoteContent, ctx);
                nodes.Add(new QuoteNode(children));
                i = len; // Block quote consumes remainder.
                continue;
            }

            // ── Single-line quote (> ) ─────────────────────────────────────────
            if (atLineStart && text.AsSpan(i).StartsWith(LineQuotePrefix, StringComparison.Ordinal))
            {
                FlushText();
                var lineEnd = text.IndexOf('\n', i + LineQuotePrefix.Length);
                var quoteLineContent = lineEnd >= 0
                    ? text[(i + LineQuotePrefix.Length)..lineEnd]
                    : text[(i + LineQuotePrefix.Length)..];
                var children = Scan(quoteLineContent, ctx);
                nodes.Add(new QuoteNode(children));
                i = lineEnd >= 0 ? lineEnd : len;
                continue;
            }

            // ── Formatting delimiters ──────────────────────────────────────────
            if (i + 2 < len && text[i] == '*' && text[i + 1] == '*' && text[i + 2] == '*')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "***", FormattingKind.Bold, ctx, out var advance);
                if (fmt is not null)
                {
                    // Bold-italic: wrap inner content in Italic inside Bold.
                    // Produce a single BoldItalic by nesting Bold > Italic.
                    var italic = new FormattingNode(FormattingKind.Italic, fmt.Children);
                    nodes.Add(new FormattingNode(FormattingKind.Bold, [italic]));
                    i += advance;
                    continue;
                }
            }

            if (i + 1 < len && text[i] == '*' && text[i + 1] == '*')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "**", FormattingKind.Bold, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            if (text[i] == '*')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "*", FormattingKind.Italic, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            if (i + 1 < len && text[i] == '_' && text[i + 1] == '_')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "__", FormattingKind.Underline, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            if (text[i] == '_')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "_", FormattingKind.Italic, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            if (i + 1 < len && text[i] == '~' && text[i + 1] == '~')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "~~", FormattingKind.Strikethrough, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            if (i + 1 < len && text[i] == '|' && text[i + 1] == '|')
            {
                FlushText();
                var fmt = TryParseFormatting(text, i, "||", FormattingKind.Spoiler, ctx, out var advance);
                if (fmt is not null) { nodes.Add(fmt); i += advance; continue; }
            }

            // ── Markdown link [display](url) ───────────────────────────────────
            if (text[i] == '[')
            {
                var m = MarkdownLinkPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var display = m.Groups[1].Value;
                    var url = m.Groups[2].Value;
                    var displayChildren = Scan(display, ctx);
                    nodes.Add(new LinkNode(url, displayChildren));
                    i += m.Length;
                    continue;
                }
            }

            // ── Bare URL ───────────────────────────────────────────────────────
            if (i + 7 < len && (text[i] == 'h') &&
                (text.AsSpan(i).StartsWith("https://", StringComparison.Ordinal) ||
                 text.AsSpan(i).StartsWith("http://", StringComparison.Ordinal)))
            {
                var m = BareUrlPattern().Match(text, i, len - i);
                if (m.Success)
                {
                    FlushText();
                    var url = m.Groups[1].Value;
                    nodes.Add(new LinkNode(url, [new TextNode(url)]));
                    i += m.Length;
                    continue;
                }
            }

            // ── Unicode emoji ──────────────────────────────────────────────────
            if (TryParseUnicodeEmoji(text, i, out var emojiGlyph, out var emojiLen))
            {
                FlushText();
                nodes.Add(new EmojiNode(EmojiKind.Unicode, null, emojiGlyph, false));
                i += emojiLen;
                continue;
            }

            // ── Plain text fallthrough ─────────────────────────────────────────
            sb.Append(text[i]);
            i++;
        }

        FlushText();
        return nodes;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static CodeBlockNode? TryParseCodeBlock(string text, int start, out int advance)
    {
        advance = 0;
        var i = start + 3; // past opening ```
        var len = text.Length;

        // Optional language hint: non-whitespace chars up to first newline.
        // If the opening ``` is immediately followed by a newline (no language), skip the newline.
        // If followed by non-backtick, non-newline chars, that's the language hint — consume through the newline.
        var langEnd = i;
        while (langEnd < len && text[langEnd] != '\n' && text[langEnd] != '`')
            langEnd++;

        string? language = null;
        if (langEnd < len && text[langEnd] == '\n')
        {
            // Whether or not there's a language hint, consume through the newline.
            language = text[i..langEnd].Trim();
            if (language.Length == 0) language = null;
            i = langEnd + 1; // skip newline
        }

        // Consume until closing ```.
        var contentStart = i;
        while (i < len)
        {
            if (i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                var content = text[contentStart..i].TrimEnd('\n');
                advance = i + 3 - start;
                return new CodeBlockNode(language, content);
            }
            i++;
        }

        // Unmatched ``` — caller falls through to plain text.
        return null;
    }

    private static InlineCodeNode? TryParseInlineCode(string text, int start, out int advance)
    {
        advance = 0;
        var i = start + 1;
        var len = text.Length;

        while (i < len)
        {
            if (text[i] == '`')
            {
                var content = text[(start + 1)..i];
                advance = i + 1 - start;
                return new InlineCodeNode(content);
            }
            // Inline code cannot span lines in Discord.
            if (text[i] == '\n') return null;
            i++;
        }

        return null;
    }

    private static FormattingNode? TryParseFormatting(
        string text, int start, string delimiter, FormattingKind kind,
        ScanContext ctx, out int advance)
    {
        advance = 0;
        var dLen = delimiter.Length;
        var i = start + dLen;
        var len = text.Length;

        // Search for closing delimiter — must not be immediately adjacent (empty spans invalid).
        while (i < len)
        {
            if (text.AsSpan(i).StartsWith(delimiter, StringComparison.Ordinal) && i > start + dLen)
            {
                var inner = text[(start + dLen)..i];
                var children = Scan(inner, ctx);
                advance = i + dLen - start;
                return new FormattingNode(kind, children);
            }
            i++;
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse a Unicode emoji starting at <paramref name="pos"/>.
    ///
    /// Coverage: standard emoji sequences including ZWJ sequences and variation selectors
    /// (VS-16, U+FE0F) that turn base characters into emoji presentations. The approach is:
    ///   1. Decode a Rune; reject surrogates and simple ASCII/Latin ranges.
    ///   2. Check if the Rune falls in known emoji Unicode blocks.
    ///   3. Consume a VS-16 if present (U+FE0F makes base chars into emoji; part of the glyph).
    ///   4. Consume ZWJ sequences (U+200D + next emoji rune) so compound emoji (family,
    ///      profession, skin-tone) are captured as a single EmojiNode.
    ///
    /// Not covered: Regional Indicator pairs (flag emoji, U+1F1E6..U+1F1FF pairs).
    /// These would need look-ahead for a paired second RI char. They appear as two
    /// TextNode characters with flag appearance in renderers — acceptable for v1.
    /// Keycap emoji (digit + U+FE0F + U+20E3) are also not captured as EmojiNode.
    /// </summary>
    private static bool TryParseUnicodeEmoji(string text, int pos, out string glyph, out int charLen)
    {
        glyph = string.Empty;
        charLen = 0;

        if (pos >= text.Length) return false;

        if (!Rune.TryGetRuneAt(text, pos, out var rune))
            return false;

        if (!IsEmojiRune(rune))
            return false;

        var sb = new StringBuilder();
        var i = pos;

        // Consume the initial rune.
        AppendRune(sb, rune);
        i += rune.Utf16SequenceLength;

        // Optional VS-16 (variation selector; makes character emoji-presentation).
        if (i < text.Length && Rune.TryGetRuneAt(text, i, out var vs16) && vs16.Value == 0xFE0F)
        {
            AppendRune(sb, vs16);
            i += vs16.Utf16SequenceLength;
        }

        // Skin tone modifiers U+1F3FB..U+1F3FF.
        if (i < text.Length && Rune.TryGetRuneAt(text, i, out var skin) &&
            skin.Value is >= 0x1F3FB and <= 0x1F3FF)
        {
            AppendRune(sb, skin);
            i += skin.Utf16SequenceLength;
        }

        // ZWJ sequences: U+200D followed by another emoji rune, repeated.
        while (i + 1 < text.Length &&
               Rune.TryGetRuneAt(text, i, out var zwj) && zwj.Value == 0x200D)
        {
            var zwjAdvance = zwj.Utf16SequenceLength;
            var nextPos = i + zwjAdvance;
            if (nextPos < text.Length &&
                Rune.TryGetRuneAt(text, nextPos, out var next) && IsEmojiRune(next))
            {
                AppendRune(sb, zwj);
                AppendRune(sb, next);
                i = nextPos + next.Utf16SequenceLength;

                // VS-16 after ZWJ component.
                if (i < text.Length && Rune.TryGetRuneAt(text, i, out var vs2) && vs2.Value == 0xFE0F)
                {
                    AppendRune(sb, vs2);
                    i += vs2.Utf16SequenceLength;
                }
            }
            else break;
        }

        glyph = sb.ToString();
        charLen = i - pos;
        return true;
    }

    private static bool IsEmojiRune(Rune r)
    {
        var v = r.Value;

        // Range checks: large emoji blocks
        if (v is (>= 0x1F300 and <= 0x1F9FF) or   // Misc Symbols + Emoticons
                 (>= 0x1FA00 and <= 0x1FAFF) or   // Supplemental Symbols
                 (>= 0x2700 and <= 0x27BF)  or   // Dingbats
                 (>= 0x2600 and <= 0x26FF)  or   // Misc Symbols
                 (>= 0x1F680 and <= 0x1F6FF) or  // Transport + Map
                 (>= 0x1F100 and <= 0x1F1FF) or  // Enclosed Alphanumeric Supplement
                 (>= 0x1F000 and <= 0x1F02F) or  // Mahjong / dominos / cards
                 (>= 0x1F1E0 and <= 0x1F1FF))    // Regional Indicators (flags)
        {
            return true;
        }

        // Common single-char emoji: hearts, stars, arrows, check marks, etc.
        return v is 0x2764 or 0x2665 or 0x2666 or 0x2663 or 0x2660
                 or 0x2B50 or 0x2B55 or 0x274C or 0x2705 or 0x2714
                 or 0x2728 or 0x231B or 0x23F0 or 0x25AA or 0x25AB
                 or 0x25B6 or 0x25C0 or 0x2934 or 0x2935;
    }

    private static void AppendRune(StringBuilder sb, Rune rune)
    {
        Span<char> buf = stackalloc char[2];
        var written = rune.EncodeToUtf16(buf);
        sb.Append(buf[..written]);
    }

    private static TimestampStyle ParseTimestampStyle(string styleChar) =>
        styleChar switch
        {
            "t" => TimestampStyle.ShortTime,
            "T" => TimestampStyle.LongTime,
            "d" => TimestampStyle.ShortDate,
            "D" => TimestampStyle.LongDate,
            "f" => TimestampStyle.ShortDateTime,
            "F" => TimestampStyle.LongDateTime,
            "R" => TimestampStyle.Relative,
            _   => TimestampStyle.ShortDateTime, // Discord default per docs
        };
}
