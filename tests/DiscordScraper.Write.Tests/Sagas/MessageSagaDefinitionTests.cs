using DiscordScraper.Write.Sagas;

namespace DiscordScraper.Write.Tests.Sagas;

/// <summary>
/// Contract tests for saga definition endpoint configuration. These tests document the
/// configuration shape and catch accidental removal of concurrency bounds without requiring
/// a full Mongo harness. The retry policy and outbox wiring are endpoint-level MT middleware
/// that can only be exercised meaningfully in an integration test.
/// </summary>
[TestFixture]
public sealed class MessageSagaDefinitionTests
{
    [Test]
    public void MessageSagaDefinition_ConcurrentMessageLimit_Is8()
    {
        // ConcurrentMessageLimit=8 is the concurrency bound chosen to reduce the rate of
        // same-correlation Mongo insert races during initial backfill bursts. Regression guard.
        var definition = new MessageSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(8));
    }

    [Test]
    public void GuildSagaDefinition_ConcurrentMessageLimit_Is8()
    {
        var definition = new GuildSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(8));
    }

    [Test]
    public void ChannelSagaDefinition_ConcurrentMessageLimit_Is8()
    {
        var definition = new ChannelSagaDefinition();
        Assert.That(definition.ConcurrentMessageLimit, Is.EqualTo(8));
    }
}
