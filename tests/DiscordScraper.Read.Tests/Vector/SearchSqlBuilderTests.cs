using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Vector;

namespace DiscordScraper.Read.Tests.Vector;

[TestFixture]
public sealed class SearchSqlBuilderTests
{
    [Test]
    public void NoFilter_ProducesNoWhereClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(), topK: 10);

        result.Sql.ShouldContain("FROM message_vectors\nORDER BY");
        result.Sql.ShouldNotContain("WHERE");
    }

    [Test]
    public void NoFilter_OnlyTopKParameter()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(), topK: 10);

        // Builder emits only top_k; @query is added by PgVectorStore after the fact.
        result.Parameters.Count.ShouldBe(1);
        result.Parameters.ShouldContain(p => p.Name == "top_k");
    }

    [Test]
    public void GuildIdFilter_ProducesGuildWhereClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(GuildId: 42L), topK: 5);

        result.Sql.ShouldContain("WHERE guild_id = @guild_id");
        result.Parameters.ShouldContain(p => p.Name == "guild_id" && (long)p.Value == 42L);
    }

    [Test]
    public void ChannelIdFilter_ProducesChannelWhereClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(ChannelId: 99L), topK: 5);

        result.Sql.ShouldContain("WHERE channel_id = @channel_id");
        result.Parameters.ShouldContain(p => p.Name == "channel_id" && (long)p.Value == 99L);
    }

    [Test]
    public void GuildAndChannelFilter_ProducesBothClauses()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(GuildId: 1L, ChannelId: 2L), topK: 5);

        result.Sql.ShouldContain("guild_id = @guild_id");
        result.Sql.ShouldContain("channel_id = @channel_id");
        result.Sql.ShouldContain("AND");
    }

    [Test]
    public void AfterFilter_ProducesCreatedAtGt()
    {
        var ts = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var result = SearchSqlBuilder.Build(new VectorFilter(After: ts), topK: 5);

        result.Sql.ShouldContain("created_at > @after");
        result.Parameters.ShouldContain(p => p.Name == "after");
    }

    [Test]
    public void BeforeFilter_ProducesCreatedAtLt()
    {
        var ts = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var result = SearchSqlBuilder.Build(new VectorFilter(Before: ts), topK: 5);

        result.Sql.ShouldContain("created_at < @before");
        result.Parameters.ShouldContain(p => p.Name == "before");
    }

    [Test]
    public void TagsFilter_ProducesArrayOverlapClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(AnyTags: ["dotnet", "csharp"]), topK: 5);

        result.Sql.ShouldContain("tags && @any_tags");
        result.Parameters.ShouldContain(p => p.Name == "any_tags");
    }

    [Test]
    public void NullTags_DoesNotProduceTagsFilterClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(AnyTags: null), topK: 5);

        result.Sql.ShouldNotContain("tags && @any_tags");
        result.Parameters.ShouldNotContain(p => p.Name == "any_tags");
    }

    [Test]
    public void EmptyTags_DoesNotProduceTagsFilterClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(AnyTags: []), topK: 5);

        result.Sql.ShouldNotContain("tags && @any_tags");
        result.Parameters.ShouldNotContain(p => p.Name == "any_tags");
    }

    [Test]
    public void AllFourFilters_ProduceFourWhereClauses()
    {
        var after  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var filter = new VectorFilter(
            GuildId:   1L,
            ChannelId: 2L,
            After:     after,
            Before:    before,
            AnyTags:   ["x"]);

        var result = SearchSqlBuilder.Build(filter, topK: 20);

        result.Sql.ShouldContain("guild_id = @guild_id");
        result.Sql.ShouldContain("channel_id = @channel_id");
        result.Sql.ShouldContain("created_at > @after");
        result.Sql.ShouldContain("created_at < @before");
        result.Sql.ShouldContain("tags && @any_tags");
        // 5 filter params + top_k
        result.Parameters.Count.ShouldBe(6);
    }

    [Test]
    public void TopK_IsAlwaysLastParameter()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(GuildId: 1L), topK: 15);

        result.Parameters[^1].Name.ShouldBe("top_k");
        result.Parameters[^1].Value.ShouldBe(15);
    }

    [Test]
    public void Sql_AlwaysEndsWithLimitClause()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(), topK: 10);
        result.Sql.ShouldContain("LIMIT @top_k;");
    }

    [Test]
    public void Sql_AlwaysContainsOrderByEmbedding()
    {
        var result = SearchSqlBuilder.Build(new VectorFilter(), topK: 10);
        result.Sql.ShouldContain("ORDER BY embedding <=> @query");
    }
}
