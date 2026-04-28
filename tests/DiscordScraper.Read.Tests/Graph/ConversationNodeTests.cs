using DiscordScraper.Core.Graph;

namespace DiscordScraper.Read.Tests.Graph;

[TestFixture]
public sealed class ConversationNodeTests
{
    private static ConversationNode MakeNode(
        long messageId = 1L,
        long? replyToId = null,
        int depth = 0) =>
        new(
            MessageId:  messageId,
            ReplyToId:  replyToId,
            ChannelId:  100L,
            GuildId:    200L,
            AuthorId:   300L,
            CreatedAt:  new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Depth:      depth);

    [Test]
    public void EqualNodes_WithSameValues_AreEqual()
    {
        var a = MakeNode();
        var b = MakeNode();
        a.ShouldBe(b);
    }

    [Test]
    public void DifferentMessageId_NotEqual()
    {
        MakeNode(messageId: 1L).ShouldNotBe(MakeNode(messageId: 2L));
    }

    [Test]
    public void NullReplyToId_VsPopulated_NotEqual()
    {
        MakeNode(replyToId: null).ShouldNotBe(MakeNode(replyToId: 99L));
    }

    [Test]
    public void DifferentDepth_NotEqual()
    {
        MakeNode(depth: 0).ShouldNotBe(MakeNode(depth: 1));
    }

    [Test]
    public void GetHashCode_SameForEqualNodes()
    {
        MakeNode().GetHashCode().ShouldBe(MakeNode().GetHashCode());
    }
}
