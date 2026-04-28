using DiscordScraper.Core.Search;
using DiscordScraper.Read.Search;

namespace DiscordScraper.Read.Tests.Search;

/// <summary>
/// Surface-level contract tests that don't require a live Postgres.
/// Real round-trips defer to integration tests.
/// </summary>
[TestFixture]
public sealed class PgSearchServiceContractTests
{
    [Test]
    public void PgSearchService_ImplementsISearchService()
    {
        typeof(PgSearchService).GetInterfaces()
            .ShouldContain(typeof(ISearchService));
    }

    [Test]
    public void PgSearchService_IsSealed()
    {
        typeof(PgSearchService).IsSealed.ShouldBeTrue();
    }

    [Test]
    public void SearchQuery_IsSealed()
    {
        typeof(SearchQuery).IsSealed.ShouldBeTrue();
    }

    [Test]
    public void SearchResult_IsSealed()
    {
        typeof(SearchResult).IsSealed.ShouldBeTrue();
    }

    [Test]
    public void SearchHit_IsSealed()
    {
        typeof(SearchHit).IsSealed.ShouldBeTrue();
    }
}
