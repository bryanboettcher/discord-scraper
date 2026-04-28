using System.Text.Json;
using DiscordScraper.Contracts.IR;

namespace DiscordScraper.Read.Tests.Data;

/// <summary>
/// Verifies that the STJ serialization used by the IR jsonb ValueConverter round-trips
/// the full MessageIR hierarchy correctly. The [JsonPolymorphic] / [JsonDerivedType]
/// attributes on MessageNode drive discriminator-based polymorphism; this test proves
/// they survive a JSON round-trip before any DB is involved.
/// </summary>
[TestFixture]
public sealed class IrValueConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Test]
    public void MessageIR_RoundTrips_TextAndFormattingNodes()
    {
        var ir = new MessageIR(
            Body: [
                new TextNode("hello "),
                new FormattingNode(FormattingKind.Bold, [new TextNode("world")]),
            ],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(ir, Options);
        var restored = JsonSerializer.Deserialize<MessageIR>(json, Options);

        restored.ShouldNotBeNull();
        restored.ShouldBe(ir);
    }

    [Test]
    public void MessageIR_RoundTrips_AllNodeTypes()
    {
        var ir = new MessageIR(
            Body: [
                new TextNode("plain"),
                new MentionNode(MentionKind.User, 123456789L, "SomeUser"),
                new ChannelRefNode(987654321L, "general"),
                new EmojiNode(EmojiKind.Custom, 111L, "wave", false),
                new TimestampNode(DateTimeOffset.UnixEpoch, TimestampStyle.Relative),
                new CodeBlockNode("csharp", "var x = 1;"),
                new InlineCodeNode("x"),
                new QuoteNode([new TextNode("quoted")]),
                new LinkNode("https://example.com", [new TextNode("click")]),
            ],
            Attachments: [new AttachmentIR(999L, "https://cdn.example.com/img.png", "image/png", 4096L, "img.png")],
            Embeds: [new EmbedIR(0, "rich", "https://example.com", "Title", "Desc")],
            ReplyTo: null,
            CapturedAt: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(ir, Options);
        var restored = JsonSerializer.Deserialize<MessageIR>(json, Options);

        restored.ShouldNotBeNull();
        restored.Body.Count.ShouldBe(ir.Body.Count);
        restored.Attachments.Count.ShouldBe(1);
        restored.Embeds.Count.ShouldBe(1);

        // Spot-check polymorphic nodes survived deserialization with correct runtime types.
        restored.Body[0].ShouldBeOfType<TextNode>();
        restored.Body[1].ShouldBeOfType<MentionNode>();
        restored.Body[2].ShouldBeOfType<ChannelRefNode>();
        restored.Body[3].ShouldBeOfType<EmojiNode>();
        restored.Body[4].ShouldBeOfType<TimestampNode>();
        restored.Body[5].ShouldBeOfType<CodeBlockNode>();
        restored.Body[6].ShouldBeOfType<InlineCodeNode>();
        restored.Body[7].ShouldBeOfType<QuoteNode>();
        restored.Body[8].ShouldBeOfType<LinkNode>();
    }

    [Test]
    public void MessageIR_Json_ContainsDiscriminatorProperty()
    {
        var ir = new MessageIR(
            Body: [new MentionNode(MentionKind.Role, 42L, "mods")],
            Attachments: [],
            Embeds: [],
            ReplyTo: null,
            CapturedAt: DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(ir, Options);

        // The $t discriminator must be present for polymorphic deserialization to work.
        json.ShouldContain("\"$t\"");
        json.ShouldContain("\"mention\"");
    }
}
