using DiscordScraper.Core.Search;
using DiscordScraper.Read.Search;

namespace DiscordScraper.Read.Tests.Search;

[TestFixture]
public sealed class SearchSqlBuilderTests
{
    // ----------------------------------------------------------------
    // WHERE clause emission
    // ----------------------------------------------------------------

    [Test]
    public void NoFilters_WhereClauseContainsOnlyTsvMatch()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("hello"));

        result.MainSql.ShouldContain("WHERE tsv @@ query");

        // WHERE section should have no AND-joined filter predicates
        var whereSection = result.MainSql[result.MainSql.IndexOf("WHERE", StringComparison.Ordinal)..];
        whereSection.ShouldNotContain("guild_id = @guild_id");
        whereSection.ShouldNotContain("channel_id = @channel_id");
        whereSection.ShouldNotContain("author_id = @author_id");
        whereSection.ShouldNotContain("created_at >");
        whereSection.ShouldNotContain("created_at <");
    }

    [Test]
    public void GuildIdFilter_AddsGuildClause()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", GuildId: 42L));

        result.MainSql.ShouldContain("guild_id = @guild_id");
        result.Parameters.ShouldContain(p => p.ParameterName == "guild_id" && (long)p.Value! == 42L);
    }

    [Test]
    public void ChannelIdFilter_AddsChannelClause()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", ChannelId: 99L));

        result.MainSql.ShouldContain("channel_id = @channel_id");
        result.Parameters.ShouldContain(p => p.ParameterName == "channel_id" && (long)p.Value! == 99L);
    }

    [Test]
    public void AuthorIdFilter_AddsAuthorClause()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", AuthorId: 7L));

        result.MainSql.ShouldContain("author_id = @author_id");
        result.Parameters.ShouldContain(p => p.ParameterName == "author_id" && (long)p.Value! == 7L);
    }

    [Test]
    public void AfterFilter_AddsCreatedAtGt()
    {
        var ts = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", After: ts));

        result.MainSql.ShouldContain("created_at > @after");
        result.Parameters.ShouldContain(p => p.ParameterName == "after");
    }

    [Test]
    public void BeforeFilter_AddsCreatedAtLt()
    {
        var ts = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", Before: ts));

        result.MainSql.ShouldContain("created_at < @before");
        result.Parameters.ShouldContain(p => p.ParameterName == "before");
    }

    [Test]
    public void CompositeFilter_GuildChannelAfter_AddsThreeAndClauses()
    {
        var after = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = SearchSqlBuilder.Build(new SearchQuery("foo",
            GuildId: 1L, ChannelId: 2L, After: after));

        result.MainSql.ShouldContain("guild_id = @guild_id");
        result.MainSql.ShouldContain("channel_id = @channel_id");
        result.MainSql.ShouldContain("created_at > @after");

        // tsv @@ query + guild + channel + after = 4 AND segments
        result.MainSql.Split("AND").Length.ShouldBe(4);
    }

    // ----------------------------------------------------------------
    // Parameter set
    // ----------------------------------------------------------------

    [Test]
    public void NoFilters_ParametersAreTextAndTopK()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("hello"));

        result.Parameters.Length.ShouldBe(2);
        result.Parameters.ShouldContain(p => p.ParameterName == "text");
        result.Parameters.ShouldContain(p => p.ParameterName == "top_k");
    }

    [Test]
    public void TopK_IsAlwaysLastParameter()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", GuildId: 1L, TopK: 25));

        result.Parameters[^1].ParameterName.ShouldBe("top_k");
        result.Parameters[^1].Value.ShouldBe(25);
    }

    [Test]
    public void TextParameter_IsFirstParameter()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("search text"));

        result.Parameters[0].ParameterName.ShouldBe("text");
        result.Parameters[0].Value.ShouldBe("search text");
    }

    // ----------------------------------------------------------------
    // SQL structure
    // ----------------------------------------------------------------

    [Test]
    public void MainSql_ContainsOrderByRankDesc()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo"));
        result.MainSql.ShouldContain("ORDER BY rank DESC");
    }

    [Test]
    public void MainSql_ContainsLimitTopK()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", TopK: 20));
        result.MainSql.ShouldContain("LIMIT @top_k;");
    }

    [Test]
    public void MainSql_ContainsTsRankAndTsHeadline()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo"));
        result.MainSql.ShouldContain("ts_rank(tsv, query)");
        result.MainSql.ShouldContain("ts_headline('english', plain_text, query,");
    }

    [Test]
    public void MainSql_ContainsWebsearchToTsquery()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo"));
        result.MainSql.ShouldContain("websearch_to_tsquery('english', @text)");
    }

    [Test]
    public void CountSql_DoesNotContainOrderByOrLimit()
    {
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", GuildId: 1L));
        result.CountSql.ShouldNotContain("ORDER BY");
        result.CountSql.ShouldNotContain("LIMIT");
        result.CountSql.ShouldContain("COUNT(*)");
    }

    [Test]
    public void SnippetLength_AffectsMaxWordsInHeadlineConfig()
    {
        // SnippetLength=100 → MaxWords=10; SnippetLength=240 → MaxWords=24
        var short_ = SearchSqlBuilder.Build(new SearchQuery("foo", SnippetLength: 100));
        var long_  = SearchSqlBuilder.Build(new SearchQuery("foo", SnippetLength: 240));

        short_.MainSql.ShouldContain("MaxWords=10");
        long_.MainSql.ShouldContain("MaxWords=24");
    }

    // ----------------------------------------------------------------
    // SnippetLength clamping
    // ----------------------------------------------------------------

    [Test]
    public void SnippetLength_Zero_ClampsToMinimum()
    {
        // MaxWords=0 breaks ts_headline — must clamp to 60/10=6
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", SnippetLength: 0));
        result.MainSql.ShouldContain("MaxWords=6");
        result.MainSql.ShouldNotContain("MaxWords=0");
    }

    [Test]
    public void SnippetLength_ExceedsMaximum_ClampsToMaximum()
    {
        // SnippetLength=999999 should clamp to 1000 → MaxWords=100
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", SnippetLength: 999999));
        result.MainSql.ShouldContain("MaxWords=100");
    }

    [Test]
    public void SnippetLength_NormalRange_PassesThroughUnclamped()
    {
        // 500 is within [60, 1000] — passes through as MaxWords=50
        var result = SearchSqlBuilder.Build(new SearchQuery("foo", SnippetLength: 500));
        result.MainSql.ShouldContain("MaxWords=50");
    }
}
