using System.Text.Json;
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Contracts.Tests;

[TestFixture]
public sealed class MessageIREqualityTests
{
    private static readonly DateTimeOffset FixedAt = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MessageIR BuildIR(string text) => new(
        Body: [new TextNode(text)],
        Attachments: [],
        Embeds: [],
        ReplyTo: null,
        CapturedAt: FixedAt);

    [Test]
    public void SameContent_AreEqual()
    {
        var a = BuildIR("hello");
        var b = BuildIR("hello");

        // Records with IReadOnlyList fields are NOT reference-equal to a freshly constructed
        // copy; our explicit Equals override provides structural collection comparison.
        a.ShouldBe(b);
    }

    [Test]
    public void DifferentBodyText_AreNotEqual()
    {
        var a = BuildIR("hello");
        var b = BuildIR("world");

        a.ShouldNotBe(b);
    }

    [Test]
    public void NestedFormattingNodes_AreStructurallyCompared()
    {
        IReadOnlyList<MessageNode> children = [new TextNode("bold")];
        var a = new MessageIR(
            Body: [new FormattingNode(FormattingKind.Bold, children)],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);
        var b = new MessageIR(
            Body: [new FormattingNode(FormattingKind.Bold, [new TextNode("bold")])],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);

        a.ShouldBe(b);
    }

    [Test]
    public void DifferentAttachments_AreNotEqual()
    {
        var a = new MessageIR(
            Body: [],
            Attachments: [new AttachmentIR(1L, "https://cdn/a.png", "image/png", 1024L, "a.png")],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);
        var b = new MessageIR(
            Body: [],
            Attachments: [new AttachmentIR(2L, "https://cdn/b.png", "image/png", 2048L, "b.png")],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);

        a.ShouldNotBe(b);
    }

    [Test]
    public void WithReplyContext_EqualWhenSame()
    {
        var reply = new ReplyContext(100L, 200L, 300L);
        var a = new MessageIR(Body: [], Attachments: [], Embeds: [], ReplyTo: reply, CapturedAt: FixedAt);
        var b = new MessageIR(Body: [], Attachments: [], Embeds: [], ReplyTo: new ReplyContext(100L, 200L, 300L), CapturedAt: FixedAt);

        a.ShouldBe(b);
    }
}

[TestFixture]
public sealed class MessageIRSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset FixedAt = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void TextNode_RoundTrips()
    {
        var ir = new MessageIR(
            Body: [new TextNode("hello world")],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);

        var json = JsonSerializer.Serialize(ir, Options);
        var restored = JsonSerializer.Deserialize<MessageIR>(json, Options);

        restored.ShouldNotBeNull();
        restored.ShouldBe(ir);
    }

    [Test]
    public void PolymorphicNodes_RoundTrip()
    {
        var ir = new MessageIR(
            Body:
            [
                new TextNode("see "),
                new MentionNode(MentionKind.User, 12345L, "@someone"),
                new ChannelRefNode(99L, "#general"),
                new EmojiNode(EmojiKind.Custom, 777L, "pepega", false),
                new TimestampNode(FixedAt, TimestampStyle.Relative),
                new FormattingNode(FormattingKind.Bold, [new TextNode("bold text")]),
                new CodeBlockNode("csharp", "var x = 1;"),
                new InlineCodeNode("x"),
                new QuoteNode([new TextNode("quoted")]),
                new LinkNode("https://example.com", [new TextNode("click here")]),
            ],
            Attachments: [new AttachmentIR(1L, "https://cdn/f.png", "image/png", 512L, "f.png")],
            Embeds: [new EmbedIR(0, "rich", "https://x.com", "Title", "Desc")],
            ReplyTo: new ReplyContext(11L, 22L, 33L),
            CapturedAt: FixedAt);

        var json = JsonSerializer.Serialize(ir, Options);
        var restored = JsonSerializer.Deserialize<MessageIR>(json, Options);

        restored.ShouldNotBeNull();
        restored.ShouldBe(ir);
    }

    [Test]
    public void Discriminator_IsPresent_InSerializedJson()
    {
        var ir = new MessageIR(
            Body: [new TextNode("hi"), new MentionNode(MentionKind.Everyone, null, "@everyone")],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: FixedAt);

        var json = JsonSerializer.Serialize(ir, Options);

        json.ShouldContain("\"$t\":\"text\"");
        json.ShouldContain("\"$t\":\"mention\"");
    }
}
