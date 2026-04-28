using DiscordScraper.Core.Vector;

namespace DiscordScraper.Read.Tests.Vector;

[TestFixture]
public sealed class VectorFilterTests
{
    [Test]
    public void DefaultFilter_HasAllNullProperties()
    {
        var filter = new VectorFilter();
        filter.GuildId.ShouldBeNull();
        filter.ChannelId.ShouldBeNull();
        filter.After.ShouldBeNull();
        filter.Before.ShouldBeNull();
        filter.AnyTags.ShouldBeNull();
    }

    [Test]
    public void TwoEmptyFilters_AreEqual()
    {
        var a = new VectorFilter();
        var b = new VectorFilter();
        a.ShouldBe(b);
    }

    [Test]
    public void FiltersWithSameTags_AreEqual()
    {
        var a = new VectorFilter(AnyTags: ["dotnet", "csharp"]);
        var b = new VectorFilter(AnyTags: ["dotnet", "csharp"]);
        a.ShouldBe(b);
    }

    [Test]
    public void FiltersWithDifferentTags_NotEqual()
    {
        var a = new VectorFilter(AnyTags: ["dotnet"]);
        var b = new VectorFilter(AnyTags: ["rust"]);
        a.ShouldNotBe(b);
    }

    [Test]
    public void NullTags_VsEmptyTags_NotEqual()
    {
        var a = new VectorFilter(AnyTags: null);
        var b = new VectorFilter(AnyTags: []);
        a.ShouldNotBe(b);
    }

    [Test]
    public void DifferentGuildId_NotEqual()
    {
        var a = new VectorFilter(GuildId: 1L);
        var b = new VectorFilter(GuildId: 2L);
        a.ShouldNotBe(b);
    }

    [Test]
    public void GetHashCode_SameForEqualFilters()
    {
        var a = new VectorFilter(GuildId: 1L, AnyTags: ["x"]);
        var b = new VectorFilter(GuildId: 1L, AnyTags: ["x"]);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
