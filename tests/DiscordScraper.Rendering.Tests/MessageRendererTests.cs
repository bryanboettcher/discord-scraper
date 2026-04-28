using DiscordScraper.Contracts.IR;
using DiscordScraper.Core.Queries;
using DiscordScraper.Rendering;

namespace DiscordScraper.Rendering.Tests;

[TestFixture]
public class MessageRendererTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MessageIR Msg(params MessageNode[] nodes) =>
        new(nodes, [], [], null, DateTimeOffset.UtcNow);

    private static MessageIR MsgFull(
        IReadOnlyList<MessageNode> body,
        IReadOnlyList<AttachmentIR>? attachments = null,
        IReadOnlyList<EmbedIR>? embeds = null,
        ReplyContext? replyTo = null) =>
        new(body, attachments ?? [], embeds ?? [], replyTo, DateTimeOffset.UtcNow);

    private static RenderContext Ctx(
        Dictionary<long, string>? channels = null,
        Dictionary<long, string>? roles = null,
        Dictionary<long, string>? users = null,
        Dictionary<long, string>? emojis = null) =>
        new(
            channels ?? new Dictionary<long, string>(),
            roles    ?? new Dictionary<long, string>(),
            users    ?? new Dictionary<long, string>(),
            emojis   ?? new Dictionary<long, string>());

    // -------------------------------------------------------------------------
    // PlainText — basic nodes
    // -------------------------------------------------------------------------

    [Test]
    public void PlainText_TextNode_ReturnsText()
    {
        var result = MessageRenderer.RenderToPlainText(Msg(new TextNode("hello world")), RenderContext.Empty);
        result.ShouldBe("hello world");
    }

    [Test]
    public void PlainText_BoldFormatting_DropsMarkers()
    {
        var node = new FormattingNode(FormattingKind.Bold, [new TextNode("bold text")]);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("bold text");
    }

    [Test]
    public void PlainText_AllFormattingKinds_DropsAllMarkers()
    {
        foreach (var kind in Enum.GetValues<FormattingKind>())
        {
            var node = new FormattingNode(kind, [new TextNode("x")]);
            var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
            result.ShouldBe("x", $"Expected no markers for {kind}");
        }
    }

    [Test]
    public void PlainText_CodeBlock_ContentWithNewlines()
    {
        var node = new CodeBlockNode("csharp", "var x = 1;");
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldContain("var x = 1;");
        // no ``` fences
        result.ShouldNotContain("```");
    }

    [Test]
    public void PlainText_InlineCode_ContentOnly()
    {
        var result = MessageRenderer.RenderToPlainText(Msg(new InlineCodeNode("foo()")), RenderContext.Empty);
        result.ShouldBe("foo()");
    }

    [Test]
    public void PlainText_UserMention_ResolvesFromContext()
    {
        var node = new MentionNode(MentionKind.User, 123L, "OldName");
        var ctx = Ctx(users: new() { [123L] = "CurrentName" });
        var result = MessageRenderer.RenderToPlainText(Msg(node), ctx);
        result.ShouldBe("@CurrentName");
    }

    [Test]
    public void PlainText_UserMention_FallsBackToFallback_WhenIdNotInContext()
    {
        var node = new MentionNode(MentionKind.User, 999L, "FallbackUser");
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("@FallbackUser");
    }

    [Test]
    public void PlainText_UserMention_FallsBackToUnknown_WhenNoFallbackAndNoContext()
    {
        var node = new MentionNode(MentionKind.User, null, null);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("@unknown");
    }

    [Test]
    public void PlainText_RoleMention_ResolvesFromContext()
    {
        var node = new MentionNode(MentionKind.Role, 42L, "OldRole");
        var ctx = Ctx(roles: new() { [42L] = "Admins" });
        var result = MessageRenderer.RenderToPlainText(Msg(node), ctx);
        result.ShouldBe("@Admins");
    }

    [Test]
    public void PlainText_ChannelRef_ResolvesFromContext()
    {
        var node = new ChannelRefNode(77L, "old-channel");
        var ctx = Ctx(channels: new() { [77L] = "general" });
        var result = MessageRenderer.RenderToPlainText(Msg(node), ctx);
        result.ShouldBe("#general");
    }

    [Test]
    public void PlainText_ChannelRef_FallsBackToFallback_WhenIdNotInContext()
    {
        var node = new ChannelRefNode(77L, "fallback-channel");
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("#fallback-channel");
    }

    [Test]
    public void PlainText_EmojiUnicode_ReturnsGlyph()
    {
        var node = new EmojiNode(EmojiKind.Unicode, null, "🎉", false);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("🎉");
    }

    [Test]
    public void PlainText_EmojiCustom_ColonDelimitedName()
    {
        var node = new EmojiNode(EmojiKind.Custom, 555L, "blobcat", false);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe(":blobcat:");
    }

    [Test]
    public void PlainText_EmojiCustom_ResolvesNameFromContext()
    {
        var node = new EmojiNode(EmojiKind.Custom, 555L, "blobcat", false);
        var ctx = Ctx(emojis: new() { [555L] = "blob_cat_wink" });
        var result = MessageRenderer.RenderToPlainText(Msg(node), ctx);
        result.ShouldBe(":blob_cat_wink:");
    }

    [Test]
    public void PlainText_Timestamp_IsoDateFormat()
    {
        var ts = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var node = new TimestampNode(ts, TimestampStyle.ShortDateTime);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("2024-06-15");
    }

    [Test]
    public void PlainText_QuoteNode_PrefixesWithAngle()
    {
        var node = new QuoteNode([new TextNode("quoted text")]);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("> quoted text");
    }

    [Test]
    public void PlainText_LinkNode_DisplayPlusUrl()
    {
        var node = new LinkNode("https://example.com", [new TextNode("click here")]);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("click here (https://example.com)");
    }

    [Test]
    public void PlainText_LinkNode_BareUrl_WhenNoDisplay()
    {
        var node = new LinkNode("https://example.com", []);
        var result = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        result.ShouldBe("https://example.com");
    }

    [Test]
    public void PlainText_AttachmentsAndEmbeds_NotAppended()
    {
        var ir = MsgFull(
            [new TextNode("body")],
            attachments: [new AttachmentIR(1L, "https://cdn.discord.com/file.png", "image/png", 1024, "file.png")],
            embeds: [new EmbedIR(0, "link", "https://x.com", "A title", "A description")]);

        var result = MessageRenderer.RenderToPlainText(ir, RenderContext.Empty);
        result.ShouldBe("body");
    }

    [Test]
    public void PlainText_EveryoneAndHereMentions()
    {
        var result1 = MessageRenderer.RenderToPlainText(Msg(new MentionNode(MentionKind.Everyone, null, null)), RenderContext.Empty);
        var result2 = MessageRenderer.RenderToPlainText(Msg(new MentionNode(MentionKind.Here, null, null)), RenderContext.Empty);
        result1.ShouldBe("@everyone");
        result2.ShouldBe("@here");
    }

    // -------------------------------------------------------------------------
    // Markdown
    // -------------------------------------------------------------------------

    [Test]
    public void Markdown_Bold_AppliesDoubleStars()
    {
        var node = new FormattingNode(FormattingKind.Bold, [new TextNode("important")]);
        var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
        result.ShouldBe("**important**");
    }

    [Test]
    public void Markdown_AllFormattingKinds_ApplyCorrectMarkers()
    {
        var cases = new Dictionary<FormattingKind, string>
        {
            [FormattingKind.Bold]          = "**x**",
            [FormattingKind.Italic]        = "*x*",
            [FormattingKind.Underline]     = "__x__",
            [FormattingKind.Strikethrough] = "~~x~~",
            [FormattingKind.Spoiler]       = "||x||",
        };
        foreach (var (kind, expected) in cases)
        {
            var node = new FormattingNode(kind, [new TextNode("x")]);
            var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
            result.ShouldBe(expected, $"Unexpected output for {kind}");
        }
    }

    [Test]
    public void Markdown_CodeBlock_WithLanguage_FencedWithLanguage()
    {
        var node = new CodeBlockNode("csharp", "var x = 1;");
        var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
        result.ShouldBe("```csharp\nvar x = 1;\n```");
    }

    [Test]
    public void Markdown_CodeBlock_NoLanguage_FencedWithoutLanguage()
    {
        var node = new CodeBlockNode(null, "some code");
        var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
        result.ShouldBe("```\nsome code\n```");
    }

    [Test]
    public void Markdown_InlineCode_BacktickWrapped()
    {
        var result = MessageRenderer.RenderToMarkdown(Msg(new InlineCodeNode("foo()")), RenderContext.Empty);
        result.ShouldBe("`foo()`");
    }

    [Test]
    public void Markdown_UserMention_UsesResolvedName()
    {
        var node = new MentionNode(MentionKind.User, 123L, "OldName");
        var ctx = Ctx(users: new() { [123L] = "alice" });
        var result = MessageRenderer.RenderToMarkdown(Msg(node), ctx);
        // Resolved names in markdown format — human-readable, not Discord ID syntax
        result.ShouldBe("@alice");
    }

    [Test]
    public void Markdown_UserMention_FallsBackToFallback_WhenNotInContext()
    {
        var node = new MentionNode(MentionKind.User, 999L, "bob");
        var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
        result.ShouldBe("@bob");
    }

    [Test]
    public void Markdown_ChannelRef_ResolvesFromContext()
    {
        var node = new ChannelRefNode(77L, "old-name");
        var ctx = Ctx(channels: new() { [77L] = "announcements" });
        var result = MessageRenderer.RenderToMarkdown(Msg(node), ctx);
        result.ShouldBe("#announcements");
    }

    [Test]
    public void Markdown_LinkNode_MarkdownHref()
    {
        var node = new LinkNode("https://example.com", [new TextNode("Example")]);
        var result = MessageRenderer.RenderToMarkdown(Msg(node), RenderContext.Empty);
        result.ShouldBe("[Example](https://example.com)");
    }

    [Test]
    public void Markdown_ReplyContext_PrependedAsQuote()
    {
        var ir = MsgFull(
            [new TextNode("the response")],
            replyTo: new ReplyContext(99L, 77L, 123L));
        var ctx = Ctx(users: new() { [123L] = "alice" });
        var result = MessageRenderer.RenderToMarkdown(ir, ctx);
        result.ShouldStartWith("> ↪ replying to @alice");
    }

    [Test]
    public void Markdown_Attachments_AppendedAsLinks()
    {
        var ir = MsgFull(
            [new TextNode("see this")],
            attachments: [new AttachmentIR(1L, "https://cdn.discord.com/file.png", "image/png", 512, "file.png")]);
        var result = MessageRenderer.RenderToMarkdown(ir, RenderContext.Empty);
        result.ShouldContain("[file.png](https://cdn.discord.com/file.png)");
    }

    [Test]
    public void Markdown_Embeds_AppendedWithTitleAndDescription()
    {
        var ir = MsgFull(
            [new TextNode("check out")],
            embeds: [new EmbedIR(0, "link", "https://x.com", "Big News", "Description here")]);
        var result = MessageRenderer.RenderToMarkdown(ir, RenderContext.Empty);
        result.ShouldContain("**Big News**");
        result.ShouldContain("Description here");
    }

    // -------------------------------------------------------------------------
    // HTML
    // -------------------------------------------------------------------------

    [Test]
    public void Html_TextNode_PlainEscapedText()
    {
        var result = MessageRenderer.RenderToHtml(Msg(new TextNode("hello")), RenderContext.Empty);
        result.ShouldBe("hello");
    }

    [Test]
    public void Html_TextNode_EscapesHtmlEntities()
    {
        var node = new TextNode("<script>alert('xss')</script>");
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldNotContain("<script>");
        result.ShouldContain("&lt;script&gt;");
    }

    [Test]
    public void Html_BoldFormatting_StrongTag()
    {
        var node = new FormattingNode(FormattingKind.Bold, [new TextNode("bold")]);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("<strong>bold</strong>");
    }

    [Test]
    public void Html_AllFormattingKinds_CorrectTags()
    {
        var cases = new Dictionary<FormattingKind, (string open, string close)>
        {
            [FormattingKind.Bold]          = ("<strong>", "</strong>"),
            [FormattingKind.Italic]        = ("<em>", "</em>"),
            [FormattingKind.Underline]     = ("<u>", "</u>"),
            [FormattingKind.Strikethrough] = ("<s>", "</s>"),
            [FormattingKind.Spoiler]       = ("<span class=\"spoiler\">", "</span>"),
        };
        foreach (var (kind, (open, close)) in cases)
        {
            var node = new FormattingNode(kind, [new TextNode("x")]);
            var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
            result.ShouldBe($"{open}x{close}", $"Unexpected tag for {kind}");
        }
    }

    [Test]
    public void Html_CodeBlock_PreCodeWithLanguageClass()
    {
        var node = new CodeBlockNode("csharp", "var x = 1;");
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("<pre><code class=\"language-csharp\">var x = 1;</code></pre>");
    }

    [Test]
    public void Html_CodeBlock_NoLanguage_NoClass()
    {
        var node = new CodeBlockNode(null, "some code");
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("<pre><code>some code</code></pre>");
    }

    [Test]
    public void Html_InlineCode_CodeTag()
    {
        var result = MessageRenderer.RenderToHtml(Msg(new InlineCodeNode("foo()")), RenderContext.Empty);
        result.ShouldBe("<code>foo()</code>");
    }

    [Test]
    public void Html_UserMention_SpanWithClass()
    {
        var node = new MentionNode(MentionKind.User, 123L, "alice");
        var ctx = Ctx(users: new() { [123L] = "alice" });
        var result = MessageRenderer.RenderToHtml(Msg(node), ctx);
        result.ShouldBe("<span class=\"mention mention-user\">@alice</span>");
    }

    [Test]
    public void Html_MentionWithXssName_IsEscaped()
    {
        var node = new MentionNode(MentionKind.User, null, "<evil>");
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldNotContain("<evil>");
        result.ShouldContain("&lt;evil&gt;");
    }

    [Test]
    public void Html_ChannelRef_SpanWithClass()
    {
        var node = new ChannelRefNode(77L, null);
        var ctx = Ctx(channels: new() { [77L] = "general" });
        var result = MessageRenderer.RenderToHtml(Msg(node), ctx);
        result.ShouldBe("<span class=\"mention mention-channel\">#general</span>");
    }

    [Test]
    public void Html_EmojiCustom_WithId_RendersImgTag()
    {
        var node = new EmojiNode(EmojiKind.Custom, 555L, "blobcat", false);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldContain("<img class=\"emoji\"");
        result.ShouldContain("cdn.discordapp.com/emojis/555.png");
        result.ShouldContain("alt=\":blobcat:\"");
    }

    [Test]
    public void Html_EmojiCustomAnimated_UsesGifExtension()
    {
        var node = new EmojiNode(EmojiKind.Custom, 555L, "dancing", true);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldContain("555.gif");
    }

    [Test]
    public void Html_EmojiUnicode_ReturnsGlyph()
    {
        var node = new EmojiNode(EmojiKind.Unicode, null, "🎉", false);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("🎉");
    }

    [Test]
    public void Html_QuoteNode_BlockquoteTag()
    {
        var node = new QuoteNode([new TextNode("quoted")]);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("<blockquote>quoted</blockquote>");
    }

    [Test]
    public void Html_LinkNode_AnchorTag()
    {
        var node = new LinkNode("https://example.com", [new TextNode("click")]);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldBe("<a href=\"https://example.com\">click</a>");
    }

    [Test]
    public void Html_LinkUrl_WithAngleBrackets_IsEscaped()
    {
        // Ensure a URL with special chars doesn't inject attributes
        var node = new LinkNode("https://example.com/path?a=1&b=2", [new TextNode("link")]);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldContain("&amp;b=2");
        result.ShouldNotContain("?a=1&b=2");
    }

    [Test]
    public void Html_AttachmentImage_RendersImgTag()
    {
        var ir = MsgFull(
            [new TextNode("body")],
            attachments: [new AttachmentIR(1L, "https://cdn.discord.com/img.png", "image/png", 1024, "img.png")]);
        var result = MessageRenderer.RenderToHtml(ir, RenderContext.Empty);
        result.ShouldContain("<img class=\"attachment-image\"");
        result.ShouldContain("https://cdn.discord.com/img.png");
    }

    [Test]
    public void Html_AttachmentNonImage_RendersAnchorTag()
    {
        var ir = MsgFull(
            [new TextNode("body")],
            attachments: [new AttachmentIR(2L, "https://cdn.discord.com/doc.pdf", "application/pdf", 4096, "doc.pdf")]);
        var result = MessageRenderer.RenderToHtml(ir, RenderContext.Empty);
        result.ShouldContain("<a class=\"attachment-file\"");
        result.ShouldContain("doc.pdf");
    }

    [Test]
    public void Html_ReplyContext_RendersReplyDiv()
    {
        var ir = MsgFull(
            [new TextNode("reply body")],
            replyTo: new ReplyContext(99L, 77L, 123L));
        var ctx = Ctx(users: new() { [123L] = "bob" });
        var result = MessageRenderer.RenderToHtml(ir, ctx);
        result.ShouldContain("class=\"reply-context\"");
        result.ShouldContain("@bob");
    }

    [Test]
    public void Html_Timestamp_TimeTagWithDatetime()
    {
        var ts = new DateTimeOffset(2024, 3, 10, 14, 30, 0, TimeSpan.Zero);
        var node = new TimestampNode(ts, TimestampStyle.ShortDateTime);
        var result = MessageRenderer.RenderToHtml(Msg(node), RenderContext.Empty);
        result.ShouldStartWith("<time datetime=");
        result.ShouldContain("2024-03-10");
    }

    // -------------------------------------------------------------------------
    // Cross-format consistency
    // -------------------------------------------------------------------------

    [Test]
    public void AllFormats_EveryoneAndHere_NeverContainDivOrSpanInPlainText()
    {
        var node = new MentionNode(MentionKind.Everyone, null, null);
        var plain = MessageRenderer.RenderToPlainText(Msg(node), RenderContext.Empty);
        plain.ShouldNotContain("<");
        plain.ShouldNotContain(">");
    }

    [Test]
    public void Render_Overload_EquivalentToFormatSpecificOverloads()
    {
        var ir = Msg(new TextNode("test"), new FormattingNode(FormattingKind.Bold, [new TextNode("bold")]));
        var ctx = RenderContext.Empty;

        MessageRenderer.Render(ir, RenderFormat.PlainText, ctx)
            .ShouldBe(MessageRenderer.RenderToPlainText(ir, ctx));
        MessageRenderer.Render(ir, RenderFormat.Markdown, ctx)
            .ShouldBe(MessageRenderer.RenderToMarkdown(ir, ctx));
        MessageRenderer.Render(ir, RenderFormat.Html, ctx)
            .ShouldBe(MessageRenderer.RenderToHtml(ir, ctx));
    }
}
