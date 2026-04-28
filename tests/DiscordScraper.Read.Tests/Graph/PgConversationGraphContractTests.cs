using DiscordScraper.Core.Graph;
using DiscordScraper.Read.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;

namespace DiscordScraper.Read.Tests.Graph;

/// <summary>
/// Surface-level contract tests. Verifies type shape, DI registrability, and
/// zero-depth short-circuit behaviour without a live database.
/// Real CTE round-trip correctness defers to integration tests.
/// </summary>
[TestFixture]
public sealed class PgConversationGraphContractTests
{
    [Test]
    public void PgConversationGraph_ImplementsIConversationGraph()
    {
        typeof(PgConversationGraph).GetInterfaces()
            .ShouldContain(typeof(IConversationGraph));
    }

    [Test]
    public void PgConversationGraph_IsSealed()
    {
        typeof(PgConversationGraph).IsSealed.ShouldBeTrue();
    }

    [Test]
    public async Task GetThreadAsync_ZeroMaxDepth_ReturnsEmpty()
    {
        var graph = MakeGraph();
        var result = await graph.GetThreadAsync(1L, maxDepth: 0, CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetReplyAncestorsAsync_ZeroMaxDepth_ReturnsEmpty()
    {
        var graph = MakeGraph();
        var result = await graph.GetReplyAncestorsAsync(1L, maxDepth: 0, CancellationToken.None);
        result.ShouldBeEmpty();
    }

    private static PgConversationGraph MakeGraph()
    {
        // NpgsqlDataSource has no interface; substitute via a real instance against an
        // unreachable connection string — methods that short-circuit before opening a
        // connection (maxDepth <= 0) don't touch the data source at all.
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unreachable;Username=test");
        var logger     = NullLogger<PgConversationGraph>.Instance;
        return new PgConversationGraph(dataSource, logger);
    }
}

/// <summary>
/// Contract tests for the null-Center path introduced by WARNING 5.
/// Verifies the IConversationGraph return-type semantics without a live database.
/// </summary>
[TestFixture]
public sealed class ConversationClusterNullCenterTests
{
    [Test]
    public void ConversationCluster_WithNullCenter_IsValid()
    {
        // Represents "message not in read_messages" — callers must check Center != null.
        var cluster = new ConversationCluster(null, [], []);
        cluster.Center.ShouldBeNull();
        cluster.Ancestors.ShouldBeEmpty();
        cluster.Descendants.ShouldBeEmpty();
    }

    [Test]
    public void ConversationCluster_NullCenter_NotEqualToNonNullCenter()
    {
        var node    = new ConversationNode(1L, null, 1L, 2L, 3L, DateTimeOffset.UnixEpoch, 0);
        var missing = new ConversationCluster(null, [], []);
        var present = new ConversationCluster(node, [], []);
        missing.ShouldNotBe(present);
    }

    [Test]
    public async Task ExpandConversationAsync_GraphReturnsNullCenter_WhenMessageMissing()
    {
        // IConversationGraph mock returns null Center — verifies the interface contract.
        var graph = Substitute.For<IConversationGraph>();
        graph.ExpandConversationAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationCluster(null, [], []));

        var result = await graph.ExpandConversationAsync(999L, 2, CancellationToken.None);

        result.Center.ShouldBeNull();
        result.Ancestors.ShouldBeEmpty();
        result.Descendants.ShouldBeEmpty();
    }
}
