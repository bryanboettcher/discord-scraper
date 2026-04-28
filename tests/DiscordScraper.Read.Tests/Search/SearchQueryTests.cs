using DiscordScraper.Core.Search;

namespace DiscordScraper.Read.Tests.Search;

[TestFixture]
public sealed class SearchQueryTests
{
    [Test]
    public void Defaults_AreCorrect()
    {
        var q = new SearchQuery("hello");

        q.Text.ShouldBe("hello");
        q.GuildId.ShouldBeNull();
        q.ChannelId.ShouldBeNull();
        q.AuthorId.ShouldBeNull();
        q.After.ShouldBeNull();
        q.Before.ShouldBeNull();
        q.TopK.ShouldBe(50);
        q.SnippetLength.ShouldBe(240);
    }

    [Test]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new SearchQuery("foo", GuildId: 1L, TopK: 10);
        var b = new SearchQuery("foo", GuildId: 1L, TopK: 10);

        a.ShouldBe(b);
    }

    [Test]
    public void RecordEquality_DifferentText_AreNotEqual()
    {
        var a = new SearchQuery("foo");
        var b = new SearchQuery("bar");

        a.ShouldNotBe(b);
    }

    [Test]
    public void With_ProducesModifiedCopy()
    {
        var original = new SearchQuery("hello", TopK: 20);
        var updated  = original with { TopK = 5 };

        updated.Text.ShouldBe("hello");
        updated.TopK.ShouldBe(5);
        original.TopK.ShouldBe(20);
    }
}
