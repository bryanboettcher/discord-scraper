using DiscordScraper.Core.Search;

namespace DiscordScraper.Read.Tests.Search;

[TestFixture]
public sealed class SearchResultTests
{
    [Test]
    public void EmptyResult_IsValid()
    {
        var result = new SearchResult([], TotalEstimate: 0);

        result.Hits.ShouldBeEmpty();
        result.TotalEstimate.ShouldBe(0);
    }

    [Test]
    public void TotalEstimate_CanExceedHitCount()
    {
        // TotalEstimate reflects total matches; Hits is the capped TopK window
        var hits = new[]
        {
            new SearchHit(1L, 2L, 3L, 4L, DateTimeOffset.UtcNow, 0.9f, "snippet"),
        };
        var result = new SearchResult(hits, TotalEstimate: 9999);

        result.Hits.Count.ShouldBe(1);
        result.TotalEstimate.ShouldBe(9999);
    }

    [Test]
    public void SearchHit_RecordEquality()
    {
        var ts = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new SearchHit(1L, 2L, 3L, 4L, ts, 0.5f, "text");
        var b = new SearchHit(1L, 2L, 3L, 4L, ts, 0.5f, "text");

        a.ShouldBe(b);
    }
}
