using DiscordScraper.Contracts.IR;
using DiscordScraper.Write.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordScraper.Write.Tests.Parsing;

[TestFixture]
public sealed class MessageParserTests
{
    private static readonly DateTimeOffset At = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ParseContext DefaultCtx = new(At);
    private static IMessageParser Parser() => new MessageParser(NullLogger<MessageParser>.Instance);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MessageIR Parse(string content, ParseContext? ctx = null)
    {
        var json = $$"""{"content":{{System.Text.Json.JsonSerializer.Serialize(content)}}}""";
        return Parser().Parse(json, ctx ?? DefaultCtx);
    }

    private static List<MessageNode> Nodes(string content, ParseContext? ctx = null) =>
        Parse(content, ctx).Body.ToList();

    // ── Plain text ───────────────────────────────────────────────────────────

    [Test]
    public void Plain_text_produces_single_text_node()
    {
        var nodes = Nodes("hello world");
        nodes.Count.ShouldBe(1);
        nodes[0].ShouldBeOfType<TextNode>().Text.ShouldBe("hello world");
    }

    [Test]
    public void Empty_content_produces_empty_body()
    {
        var nodes = Nodes("");
        nodes.ShouldBeEmpty();
    }

    // ── Bold / Italic / Underline / Strikethrough / Spoiler ──────────────────

    [Test]
    public void Bold_produces_formatting_node()
    {
        var nodes = Nodes("**bold**");
        nodes.Count.ShouldBe(1);
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Bold);
        fmt.Children.Count.ShouldBe(1);
        fmt.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("bold");
    }

    [Test]
    public void Italic_asterisk_produces_formatting_node()
    {
        var nodes = Nodes("*italic*");
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Italic);
        fmt.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("italic");
    }

    [Test]
    public void Italic_underscore_produces_formatting_node()
    {
        var nodes = Nodes("_italic_");
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Italic);
    }

    [Test]
    public void Underline_produces_formatting_node()
    {
        var nodes = Nodes("__underline__");
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Underline);
        fmt.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("underline");
    }

    [Test]
    public void Strikethrough_produces_formatting_node()
    {
        var nodes = Nodes("~~strike~~");
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Strikethrough);
        fmt.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("strike");
    }

    [Test]
    public void Spoiler_produces_formatting_node()
    {
        var nodes = Nodes("||spoiler||");
        var fmt = nodes[0].ShouldBeOfType<FormattingNode>();
        fmt.Kind.ShouldBe(FormattingKind.Spoiler);
        fmt.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("spoiler");
    }

    [Test]
    public void Bold_italic_triple_asterisk_nests_italic_inside_bold()
    {
        var nodes = Nodes("***bold italic***");
        var outer = nodes[0].ShouldBeOfType<FormattingNode>();
        outer.Kind.ShouldBe(FormattingKind.Bold);
        var inner = outer.Children[0].ShouldBeOfType<FormattingNode>();
        inner.Kind.ShouldBe(FormattingKind.Italic);
        inner.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("bold italic");
    }

    [Test]
    public void Formatting_nesting_bold_contains_italic()
    {
        // **outer *inner* end**
        var nodes = Nodes("**outer *inner* end**");
        var bold = nodes[0].ShouldBeOfType<FormattingNode>();
        bold.Kind.ShouldBe(FormattingKind.Bold);
        // Children: TextNode("outer "), FormattingNode(Italic), TextNode(" end")
        bold.Children.Count.ShouldBe(3);
        bold.Children[1].ShouldBeOfType<FormattingNode>().Kind.ShouldBe(FormattingKind.Italic);
    }

    [Test]
    public void Unbalanced_bold_falls_back_to_text_node()
    {
        // Unbalanced "**" should NOT produce a FormattingNode; raw chars become TextNode.
        var nodes = Nodes("**unbalanced");
        // The unmatched opening ** plus text is just text.
        nodes.ShouldNotBeEmpty();
        // None of the nodes should be a FormattingNode.
        nodes.ShouldAllBe(n => n is TextNode);
    }

    // ── Inline code ──────────────────────────────────────────────────────────

    [Test]
    public void Inline_code_produces_inline_code_node()
    {
        var nodes = Nodes("`code`");
        nodes[0].ShouldBeOfType<InlineCodeNode>().Content.ShouldBe("code");
    }

    [Test]
    public void Inline_code_does_not_parse_markdown_inside()
    {
        // Bold inside inline code is NOT formatted.
        var nodes = Nodes("`**not bold**`");
        nodes[0].ShouldBeOfType<InlineCodeNode>().Content.ShouldBe("**not bold**");
    }

    // ── Code blocks ──────────────────────────────────────────────────────────

    [Test]
    public void Code_block_with_language_produces_code_block_node()
    {
        var nodes = Nodes("```csharp\nvar x = 1;\n```");
        var block = nodes[0].ShouldBeOfType<CodeBlockNode>();
        block.Language.ShouldBe("csharp");
        block.Content.ShouldBe("var x = 1;");
    }

    [Test]
    public void Code_block_without_language_has_null_language()
    {
        var nodes = Nodes("```\nsome code\n```");
        var block = nodes[0].ShouldBeOfType<CodeBlockNode>();
        block.Language.ShouldBeNull();
        block.Content.ShouldBe("some code");
    }

    [Test]
    public void Code_block_multiline_content_preserved()
    {
        var nodes = Nodes("```\nline1\nline2\nline3\n```");
        var block = nodes[0].ShouldBeOfType<CodeBlockNode>();
        block.Content.ShouldContain("line1");
        block.Content.ShouldContain("line2");
        block.Content.ShouldContain("line3");
    }

    // ── Quotes ───────────────────────────────────────────────────────────────

    [Test]
    public void Single_line_quote_produces_quote_node()
    {
        var nodes = Nodes("> quoted text");
        var quote = nodes[0].ShouldBeOfType<QuoteNode>();
        quote.Children.Count.ShouldBe(1);
        quote.Children[0].ShouldBeOfType<TextNode>().Text.ShouldBe("quoted text");
    }

    [Test]
    public void Block_quote_triple_gt_consumes_remainder()
    {
        var nodes = Nodes(">>> multi\nline\nquote");
        var quote = nodes[0].ShouldBeOfType<QuoteNode>();
        // The full text "multi\nline\nquote" is the quote content.
        var combined = string.Concat(quote.Children.OfType<TextNode>().Select(n => n.Text));
        combined.ShouldContain("multi");
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    [Test]
    public void Markdown_link_produces_link_node_with_display_children()
    {
        var nodes = Nodes("[click here](https://example.com)");
        var link = nodes[0].ShouldBeOfType<LinkNode>();
        link.Url.ShouldBe("https://example.com");
        link.DisplayChildren[0].ShouldBeOfType<TextNode>().Text.ShouldBe("click here");
    }

    [Test]
    public void Bare_url_produces_link_node_with_url_as_display()
    {
        var nodes = Nodes("https://example.com");
        var link = nodes[0].ShouldBeOfType<LinkNode>();
        link.Url.ShouldBe("https://example.com");
        link.DisplayChildren[0].ShouldBeOfType<TextNode>().Text.ShouldBe("https://example.com");
    }

    // ── Mentions ──────────────────────────────────────────────────────────────

    [Test]
    public void User_mention_produces_mention_node()
    {
        var nodes = Nodes("<@123456789>");
        var mention = nodes[0].ShouldBeOfType<MentionNode>();
        mention.Kind.ShouldBe(MentionKind.User);
        mention.Id.ShouldBe(123456789L);
    }

    [Test]
    public void User_mention_bang_variant_produces_mention_node()
    {
        var nodes = Nodes("<@!987654321>");
        var mention = nodes[0].ShouldBeOfType<MentionNode>();
        mention.Kind.ShouldBe(MentionKind.User);
        mention.Id.ShouldBe(987654321L);
    }

    [Test]
    public void User_mention_fallback_comes_from_payload_mentions_array()
    {
        // The mentions[] array in the payload carries the display name; parser must use it.
        var json = """
            {
                "content": "<@111222333>",
                "mentions": [
                    { "id": "111222333", "username": "alice", "global_name": "Alice Smith" }
                ]
            }
            """;
        var ir = Parser().Parse(json, DefaultCtx);
        var mention = ir.Body[0].ShouldBeOfType<MentionNode>();
        mention.Fallback.ShouldBe("Alice Smith");
    }

    [Test]
    public void User_mention_falls_back_to_username_when_no_global_name()
    {
        var json = """
            {
                "content": "<@555666777>",
                "mentions": [
                    { "id": "555666777", "username": "bobhandle" }
                ]
            }
            """;
        var ir = Parser().Parse(json, DefaultCtx);
        var mention = ir.Body[0].ShouldBeOfType<MentionNode>();
        mention.Fallback.ShouldBe("bobhandle");
    }

    [Test]
    public void Role_mention_produces_role_mention_with_null_fallback()
    {
        var nodes = Nodes("<@&444555666>");
        var mention = nodes[0].ShouldBeOfType<MentionNode>();
        mention.Kind.ShouldBe(MentionKind.Role);
        mention.Id.ShouldBe(444555666L);
        mention.Fallback.ShouldBeNull();
    }

    [Test]
    public void Everyone_mention_produces_everyone_node()
    {
        var nodes = Nodes("@everyone please read");
        var mention = nodes[0].ShouldBeOfType<MentionNode>();
        mention.Kind.ShouldBe(MentionKind.Everyone);
        mention.Id.ShouldBeNull();
        mention.Fallback.ShouldBe("@everyone");
    }

    [Test]
    public void Here_mention_produces_here_node()
    {
        var nodes = Nodes("@here we go");
        var mention = nodes[0].ShouldBeOfType<MentionNode>();
        mention.Kind.ShouldBe(MentionKind.Here);
        mention.Id.ShouldBeNull();
        mention.Fallback.ShouldBe("@here");
    }

    // ── Channel refs ──────────────────────────────────────────────────────────

    [Test]
    public void Channel_ref_produces_channel_ref_node_with_null_fallback()
    {
        var nodes = Nodes("<#999888777>");
        var chan = nodes[0].ShouldBeOfType<ChannelRefNode>();
        chan.ChannelId.ShouldBe(999888777L);
        chan.Fallback.ShouldBeNull(); // 3C resolves this
    }

    [Test]
    public void Channel_ref_populates_fallback_when_home_channel_matches()
    {
        var ctx = new ParseContext(At, HomeChannelId: 999888777L, HomeChannelName: "general");
        var nodes = Nodes("<#999888777>", ctx);
        var chan = nodes[0].ShouldBeOfType<ChannelRefNode>();
        chan.Fallback.ShouldBe("general");
    }

    [Test]
    public void Channel_ref_leaves_fallback_null_for_non_home_channel()
    {
        var ctx = new ParseContext(At, HomeChannelId: 111L, HomeChannelName: "general");
        var nodes = Nodes("<#999888777>", ctx);
        var chan = nodes[0].ShouldBeOfType<ChannelRefNode>();
        chan.Fallback.ShouldBeNull();
    }

    // ── Custom emoji ──────────────────────────────────────────────────────────

    [Test]
    public void Custom_emoji_static_produces_emoji_node()
    {
        var nodes = Nodes("<:thumbsup:123456789>");
        var emoji = nodes[0].ShouldBeOfType<EmojiNode>();
        emoji.Kind.ShouldBe(EmojiKind.Custom);
        emoji.Id.ShouldBe(123456789L);
        emoji.NameOrGlyph.ShouldBe("thumbsup");
        emoji.Animated.ShouldBeFalse();
    }

    [Test]
    public void Custom_emoji_animated_sets_animated_flag()
    {
        var nodes = Nodes("<a:dance:987654321>");
        var emoji = nodes[0].ShouldBeOfType<EmojiNode>();
        emoji.Kind.ShouldBe(EmojiKind.Custom);
        emoji.Animated.ShouldBeTrue();
        emoji.NameOrGlyph.ShouldBe("dance");
    }

    // ── Unicode emoji ─────────────────────────────────────────────────────────

    [Test]
    public void Unicode_emoji_party_produces_emoji_node()
    {
        var nodes = Nodes("🎉");
        nodes.Count.ShouldBe(1);
        var emoji = nodes[0].ShouldBeOfType<EmojiNode>();
        emoji.Kind.ShouldBe(EmojiKind.Unicode);
        emoji.Id.ShouldBeNull();
        emoji.NameOrGlyph.ShouldBe("🎉");
        emoji.Animated.ShouldBeFalse();
    }

    [Test]
    public void Unicode_emoji_mixed_with_text_produces_correct_sequence()
    {
        var nodes = Nodes("nice 🎉 work");
        nodes.Count.ShouldBe(3);
        nodes[0].ShouldBeOfType<TextNode>().Text.ShouldBe("nice ");
        nodes[1].ShouldBeOfType<EmojiNode>().NameOrGlyph.ShouldBe("🎉");
        nodes[2].ShouldBeOfType<TextNode>().Text.ShouldBe(" work");
    }

    // ── Timestamps ───────────────────────────────────────────────────────────

    [Test]
    public void Timestamp_relative_style_produces_timestamp_node()
    {
        var nodes = Nodes("<t:1700000000:R>");
        var ts = nodes[0].ShouldBeOfType<TimestampNode>();
        ts.Value.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        ts.Style.ShouldBe(TimestampStyle.Relative);
    }

    [Test]
    [TestCase("t", TimestampStyle.ShortTime)]
    [TestCase("T", TimestampStyle.LongTime)]
    [TestCase("d", TimestampStyle.ShortDate)]
    [TestCase("D", TimestampStyle.LongDate)]
    [TestCase("f", TimestampStyle.ShortDateTime)]
    [TestCase("F", TimestampStyle.LongDateTime)]
    [TestCase("R", TimestampStyle.Relative)]
    public void Timestamp_all_styles_parse_correctly(string styleChar, TimestampStyle expected)
    {
        var nodes = Nodes($"<t:1700000000:{styleChar}>");
        nodes[0].ShouldBeOfType<TimestampNode>().Style.ShouldBe(expected);
    }

    [Test]
    public void Timestamp_without_style_defaults_to_short_date_time()
    {
        var nodes = Nodes("<t:1700000000>");
        nodes[0].ShouldBeOfType<TimestampNode>().Style.ShouldBe(TimestampStyle.ShortDateTime);
    }

    // ── Full payload parsing ──────────────────────────────────────────────────

    [Test]
    public void Full_payload_with_attachments_embeds_reply_parses_correctly()
    {
        const string json = """
            {
                "id": "1234567890",
                "content": "check this out",
                "author": {
                    "id": "99999",
                    "username": "testuser",
                    "global_name": "Test User"
                },
                "attachments": [
                    {
                        "id": "111",
                        "url": "https://cdn.discordapp.com/attachments/111/222/file.png",
                        "content_type": "image/png",
                        "size": 4096,
                        "filename": "file.png"
                    }
                ],
                "embeds": [
                    {
                        "type": "rich",
                        "url": "https://example.com",
                        "title": "Example Title",
                        "description": "An example embed"
                    }
                ],
                "referenced_message": {
                    "id": "5555555555",
                    "channel_id": "6666666666",
                    "author": {
                        "id": "7777777777"
                    }
                },
                "mentions": []
            }
            """;

        var ir = Parser().Parse(json, DefaultCtx);

        // Body
        ir.Body.Count.ShouldBe(1);
        ir.Body[0].ShouldBeOfType<TextNode>().Text.ShouldBe("check this out");

        // Attachments
        ir.Attachments.Count.ShouldBe(1);
        var att = ir.Attachments[0];
        att.Id.ShouldBe(111L);
        att.ContentType.ShouldBe("image/png");
        att.SizeBytes.ShouldBe(4096L);
        att.Filename.ShouldBe("file.png");

        // Embeds
        ir.Embeds.Count.ShouldBe(1);
        var emb = ir.Embeds[0];
        emb.Index.ShouldBe(0);
        emb.Type.ShouldBe("rich");
        emb.Title.ShouldBe("Example Title");
        emb.Description.ShouldBe("An example embed");

        // Reply context
        ir.ReplyTo.ShouldNotBeNull();
        ir.ReplyTo!.ReplyToMessageId.ShouldBe(5555555555L);
        ir.ReplyTo.ReplyToChannelId.ShouldBe(6666666666L);
        ir.ReplyTo.ReplyToAuthorId.ShouldBe(7777777777L);

        // CapturedAt
        ir.CapturedAt.ShouldBe(At);
    }

    [Test]
    public void Empty_payload_json_returns_empty_ir()
    {
        var ir = Parser().Parse(string.Empty, DefaultCtx);
        ir.Body.ShouldBeEmpty();
        ir.Attachments.ShouldBeEmpty();
        ir.Embeds.ShouldBeEmpty();
        ir.ReplyTo.ShouldBeNull();
    }

    [Test]
    public void Malformed_json_returns_empty_ir_without_throwing()
    {
        Should.NotThrow(() =>
        {
            var ir = Parser().Parse("{not valid json", DefaultCtx);
            ir.Body.ShouldBeEmpty();
        });
    }

    [Test]
    public void Body_with_only_emoji_and_mentions_produces_no_text_nodes()
    {
        var json = """
            {
                "content": "<@123> 🎉 <:heart:999>",
                "mentions": [
                    { "id": "123", "username": "alice" }
                ]
            }
            """;
        var ir = Parser().Parse(json, DefaultCtx);
        // The body should have at least one MentionNode; no unexpected word-content TextNodes.
        var textNodes = ir.Body.OfType<TextNode>().ToList();
        textNodes.ShouldAllBe(t => t.Text.All(c => c == ' '));
        ir.Body.OfType<MentionNode>().ShouldHaveSingleItem();
    }
}
