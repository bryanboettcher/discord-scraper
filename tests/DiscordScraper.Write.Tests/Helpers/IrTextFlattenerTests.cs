using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Write.Tests.Helpers;

[TestFixture]
public sealed class IrTextFlattenerTests
{
    private static MessageIR MakeIR(params MessageNode[] body) =>
        new(Body: body, Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: DateTimeOffset.UtcNow);

    // -------------------------------------------------------------------------
    // Single-node cases
    // -------------------------------------------------------------------------

    [Test]
    public void Single_TextNode_returns_text()
    {
        var ir = MakeIR(new TextNode("hello world"));
        IrTextFlattener.Flatten(ir).ShouldBe("hello world");
    }

    [Test]
    public void FormattingNode_wrapping_text_flattens_to_inner_text_only()
    {
        // Bold markers should not appear in the output — embeddings care about
        // semantic tokens, not markdown syntax.
        var node = new FormattingNode(FormattingKind.Bold, [new TextNode("important")]);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("important");
    }

    [Test]
    public void CodeBlock_with_language_includes_content_strips_language()
    {
        var node = new CodeBlockNode("csharp", "var x = 1;");
        var ir = MakeIR(node);
        // Language is metadata; content is the semantic payload.
        IrTextFlattener.Flatten(ir).ShouldContain("var x = 1;");
        IrTextFlattener.Flatten(ir).ShouldNotContain("csharp");
    }

    [Test]
    public void CodeBlock_without_language_includes_content()
    {
        var node = new CodeBlockNode(null, "SELECT 1");
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldContain("SELECT 1");
    }

    [Test]
    public void Mention_with_Fallback_emits_at_fallback()
    {
        var node = new MentionNode(MentionKind.User, 999L, "alice");
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("@alice");
    }

    [Test]
    public void Mention_without_Fallback_emits_at_id()
    {
        var node = new MentionNode(MentionKind.User, 42L, null);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("@42");
    }

    [Test]
    public void Mention_without_Fallback_or_Id_emits_at_unknown()
    {
        var node = new MentionNode(MentionKind.Everyone, null, null);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("@unknown");
    }

    [Test]
    public void ChannelRef_with_Fallback_emits_hash_fallback()
    {
        var node = new ChannelRefNode(7777L, "general");
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("#general");
    }

    [Test]
    public void ChannelRef_without_Fallback_emits_hash_id()
    {
        var node = new ChannelRefNode(7777L, null);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("#7777");
    }

    // -------------------------------------------------------------------------
    // Mixed-node tree
    // -------------------------------------------------------------------------

    [Test]
    public void Mixed_nodes_concatenate_in_order()
    {
        var ir = MakeIR(
            new TextNode("hey "),
            new MentionNode(MentionKind.User, 1L, "bob"),
            new TextNode(" check "),
            new ChannelRefNode(100L, "announcements"));

        IrTextFlattener.Flatten(ir).ShouldBe("hey @bob check #announcements");
    }

    [Test]
    public void QuoteNode_prefixes_content_with_angle_bracket()
    {
        var node = new QuoteNode([new TextNode("quoted text")]);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("> quoted text");
    }

    [Test]
    public void LinkNode_with_display_text_includes_url_in_parens()
    {
        var node = new LinkNode("https://example.com", [new TextNode("click here")]);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("click here (https://example.com)");
    }

    [Test]
    public void LinkNode_without_display_text_emits_bare_url()
    {
        var node = new LinkNode("https://example.com", []);
        var ir = MakeIR(node);
        IrTextFlattener.Flatten(ir).ShouldBe("https://example.com");
    }

    [Test]
    public void Empty_IR_returns_empty_string()
    {
        var ir = MakeIR();
        IrTextFlattener.Flatten(ir).ShouldBe(string.Empty);
    }
}
