using DiscordScraper.Core.Vector;
using DiscordScraper.Read.Vector;

namespace DiscordScraper.Read.Tests.Vector;

/// <summary>
/// Surface-level contract tests that don't require a live Postgres.
/// Verifies the public type surface is wired correctly.
/// </summary>
[TestFixture]
public sealed class PgVectorStoreContractTests
{
    [Test]
    public void PgVectorStore_ImplementsIVectorStore()
    {
        typeof(PgVectorStore).GetInterfaces()
            .ShouldContain(typeof(IVectorStore));
    }

    [Test]
    public void PgVectorStore_IsSealed()
    {
        typeof(PgVectorStore).IsSealed.ShouldBeTrue();
    }

    [Test]
    public void MessageVectorsSchemaInitializer_IsSealed()
    {
        typeof(MessageVectorsSchemaInitializer).IsSealed.ShouldBeTrue();
    }
}
