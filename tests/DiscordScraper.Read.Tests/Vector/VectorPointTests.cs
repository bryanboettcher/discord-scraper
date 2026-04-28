using DiscordScraper.Core.Vector;

namespace DiscordScraper.Read.Tests.Vector;

[TestFixture]
public sealed class VectorPointTests
{
    private static VectorPoint MakePoint(long messageId = 1L, float[]? embedding = null, string[]? tags = null) =>
        new(
            MessageId: messageId,
            Embedding: (embedding ?? [0.1f, 0.2f, 0.3f]),
            ChannelId: 100L,
            GuildId: 200L,
            AuthorId: 300L,
            CreatedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Tags: tags ?? ["csharp", "dotnet"]);

    [Test]
    public void EqualPoints_WithSameValues_AreEqual()
    {
        var a = MakePoint();
        var b = MakePoint();
        a.ShouldBe(b);
    }

    [Test]
    public void DifferentMessageId_NotEqual()
    {
        var a = MakePoint(messageId: 1L);
        var b = MakePoint(messageId: 2L);
        a.ShouldNotBe(b);
    }

    [Test]
    public void DifferentEmbedding_NotEqual()
    {
        var a = MakePoint(embedding: [0.1f, 0.2f]);
        var b = MakePoint(embedding: [0.9f, 0.8f]);
        a.ShouldNotBe(b);
    }

    [Test]
    public void DifferentTags_NotEqual()
    {
        var a = MakePoint(tags: ["csharp"]);
        var b = MakePoint(tags: ["rust"]);
        a.ShouldNotBe(b);
    }

    [Test]
    public void SameTagsInOrder_AreEqual()
    {
        var a = MakePoint(tags: ["a", "b", "c"]);
        var b = MakePoint(tags: ["a", "b", "c"]);
        a.ShouldBe(b);
    }

    [Test]
    public void EmptyTags_EqualToEmptyTags()
    {
        var a = MakePoint(tags: []);
        var b = MakePoint(tags: []);
        a.ShouldBe(b);
    }

    [Test]
    public void GetHashCode_SameForEqualPoints()
    {
        var a = MakePoint();
        var b = MakePoint();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
