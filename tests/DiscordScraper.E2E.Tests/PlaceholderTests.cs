namespace DiscordScraper.E2E.Tests;

/// <summary>
/// End-to-end tests run real Postgres + Mongo + RabbitMQ via Testcontainers and exercise
/// the full saga path Captured → Enriched against a seeded message corpus.
///
/// Excluded from the main <c>DiscordScraper.slnx</c> by design — Testcontainers boot
/// time would dominate the inner-loop unit-test feedback loop. Run via:
///   <code>dotnet test DiscordScraper.E2E.slnx</code>
///
/// MassTransit in-memory harness tests for individual sagas and consumers live in
/// their respective <c>*.Tests</c> projects and ARE part of the default suite —
/// they don't need containers.
/// </summary>
[TestFixture]
public sealed class PlaceholderTests
{
    [Test]
    public void Sentinel_KeepsTheProjectCompiling()
    {
        true.ShouldBeTrue();
    }
}
