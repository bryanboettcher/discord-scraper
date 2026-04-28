using DiscordScraper.Read.Graph;

namespace DiscordScraper.Read.Tests.Graph;

/// <summary>
/// Asserts that each SQL constant contains the structural markers expected for correctness.
/// Guards against accidental truncation or missing clauses during refactors.
/// </summary>
[TestFixture]
public sealed class ConversationGraphSqlTests
{
    [Test]
    public void GetThreadSql_ContainsRecursiveCte()
    {
        ConversationGraphSqlBuilder.GetThreadSql.ShouldContain("WITH RECURSIVE thread");
    }

    [Test]
    public void GetThreadSql_JoinsOnReplyToId()
    {
        ConversationGraphSqlBuilder.GetThreadSql.ShouldContain("ON m.reply_to_id = t.message_id");
    }

    [Test]
    public void GetThreadSql_BindsRootIdAndMaxDepth()
    {
        ConversationGraphSqlBuilder.GetThreadSql.ShouldContain("@root_id");
        ConversationGraphSqlBuilder.GetThreadSql.ShouldContain("@max_depth");
    }

    [Test]
    public void GetThreadSql_OrdersByCreatedAt()
    {
        ConversationGraphSqlBuilder.GetThreadSql.ShouldContain("ORDER BY created_at");
    }

    [Test]
    public void GetReplyAncestorsSql_ContainsRecursiveCte()
    {
        ConversationGraphSqlBuilder.GetReplyAncestorsSql.ShouldContain("WITH RECURSIVE ancestors");
    }

    [Test]
    public void GetReplyAncestorsSql_JoinsOnAncestorReplyToId()
    {
        // Reverse walk: parent's message_id matches child's reply_to_id
        ConversationGraphSqlBuilder.GetReplyAncestorsSql.ShouldContain("ON a.reply_to_id = m.message_id");
    }

    [Test]
    public void GetReplyAncestorsSql_BindsLeafIdAndMaxDepth()
    {
        ConversationGraphSqlBuilder.GetReplyAncestorsSql.ShouldContain("@leaf_id");
        ConversationGraphSqlBuilder.GetReplyAncestorsSql.ShouldContain("@max_depth");
    }

    [Test]
    public void GetReplyAncestorsSql_OrdersByDepthDesc()
    {
        // depth DESC puts root first, leaf last — conversation-chronological for the caller.
        ConversationGraphSqlBuilder.GetReplyAncestorsSql.ShouldContain("ORDER BY depth DESC");
    }
}
