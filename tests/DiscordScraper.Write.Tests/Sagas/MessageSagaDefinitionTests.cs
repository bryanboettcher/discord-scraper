using DiscordScraper.Write.Sagas;

namespace DiscordScraper.Write.Tests.Sagas;

/// <summary>
/// Contract tests for saga definition endpoint configuration. These tests document the
/// configuration shape and catch accidental removal of concurrency bounds without requiring
/// a full Mongo harness. The partitioner, retry policy, and outbox wiring are endpoint-level
/// MT middleware that can only be exercised meaningfully in an integration test.
/// </summary>
[TestFixture]
public sealed class MessageSagaDefinitionTests
{
    // ConcurrentMessageLimit=16 matches the per-saga partition count, removing the artificial
    // throttle that previously accommodated same-correlation contention. Partitioning prevents
    // the contention by construction (wiki ADR-001). Regression guard.

    [Test]
    public void MessageSagaDefinition_ConcurrentMessageLimit_MatchesPartitionCount()
    {
        var definition = new MessageSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(16));
    }

    [Test]
    public void GuildSagaDefinition_ConcurrentMessageLimit_MatchesPartitionCount()
    {
        var definition = new GuildSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(16));
    }

    [Test]
    public void ChannelSagaDefinition_ConcurrentMessageLimit_MatchesPartitionCount()
    {
        var definition = new ChannelSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(16));
    }
}
