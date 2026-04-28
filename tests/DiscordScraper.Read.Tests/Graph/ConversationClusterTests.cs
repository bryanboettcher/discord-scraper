using DiscordScraper.Core.Graph;

namespace DiscordScraper.Read.Tests.Graph;

[TestFixture]
public sealed class ConversationClusterTests
{
    private static ConversationNode Node(long id, int depth = 0) =>
        new(id, null, 100L, 200L, 300L, DateTimeOffset.UnixEpoch, depth);

    [Test]
    public void EqualClusters_SameCenter_SameListInstances_AreEqual()
    {
        var center    = Node(1L);
        var ancestors  = new List<ConversationNode> { Node(2L, 1) };
        var descendants = new List<ConversationNode> { Node(3L, 1) };
        // Record equality on IReadOnlyList uses reference equality for the list objects.
        // Pass the same list instances to verify the record compares references correctly.
        var a = new ConversationCluster(center, ancestors, descendants);
        var b = new ConversationCluster(center, ancestors, descendants);
        a.ShouldBe(b);
    }

    [Test]
    public void DifferentCenter_NotEqual()
    {
        var a = new ConversationCluster(Node(1L), [], []);
        var b = new ConversationCluster(Node(2L), [], []);
        a.ShouldNotBe(b);
    }

    [Test]
    public void EmptyAncestorsAndDescendants_IsValid()
    {
        var cluster = new ConversationCluster(Node(1L), [], []);
        cluster.Ancestors.Count.ShouldBe(0);
        cluster.Descendants.Count.ShouldBe(0);
    }

    [Test]
    public void GetHashCode_SameForEqualClusters()
    {
        var center     = Node(1L);
        var ancestors  = Array.Empty<ConversationNode>();
        var descendants = Array.Empty<ConversationNode>();
        var a = new ConversationCluster(center, ancestors, descendants);
        var b = new ConversationCluster(center, ancestors, descendants);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
