using DiscordScraper.Core.Graph;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DiscordScraper.Read.Graph;

/// <summary>
/// Recursive-CTE implementation of IConversationGraph backed by Postgres read_messages hypertable.
/// Stage-2 exit ramp: swap for Apache AGE or Neo4j behind the same interface when the graph
/// grows beyond what SQL recursive CTEs serve efficiently (~AGE at 500M nodes).
///
/// Cycle protection: Discord reply semantics prevent natural cycles — replies always point at
/// older messages. The depth bound in each CTE's recursive term is a defensive guard against
/// corrupt data, not a normal-path limiter.
/// </summary>
internal sealed class PgConversationGraph(NpgsqlDataSource dataSource, ILogger<PgConversationGraph> logger)
    : IConversationGraph
{
    public async Task<IReadOnlyList<ConversationNode>> GetThreadAsync(
        long rootMessageId,
        int maxDepth,
        CancellationToken ct)
    {
        if (maxDepth <= 0)
        {
            logger.LogDebug("GetThreadAsync called with maxDepth={MaxDepth}; returning empty", maxDepth);
            return [];
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ConversationGraphSqlBuilder.GetThreadSql;
        cmd.Parameters.AddWithValue("root_id", rootMessageId);
        cmd.Parameters.AddWithValue("max_depth", maxDepth);

        return await ReadNodesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<ConversationNode>> GetReplyAncestorsAsync(
        long messageId,
        int maxDepth,
        CancellationToken ct)
    {
        if (maxDepth <= 0)
        {
            logger.LogDebug("GetReplyAncestorsAsync called with maxDepth={MaxDepth}; returning empty", maxDepth);
            return [];
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ConversationGraphSqlBuilder.GetReplyAncestorsSql;
        cmd.Parameters.AddWithValue("leaf_id", messageId);
        cmd.Parameters.AddWithValue("max_depth", maxDepth);

        return await ReadNodesAsync(cmd, ct);
    }

    public async Task<ConversationCluster> ExpandConversationAsync(
        long centerMessageId,
        int radius,
        CancellationToken ct)
    {
        // Run both CTE traversals in parallel — they touch disjoint index scans.
        // The center node appears at depth=0 in both result sets; we take it from ancestors
        // (always present) and exclude depth=0 from descendants to avoid returning it twice.
        var ancestorTask   = GetReplyAncestorsAsync(centerMessageId, radius, ct);
        var descendantTask = GetThreadAsync(centerMessageId, radius, ct);
        await Task.WhenAll(ancestorTask, descendantTask);
        var ancestors   = ancestorTask.Result;
        var descendants = descendantTask.Result;

        // Ancestors result is ordered depth DESC: first element is the chain root, last is the center.
        // If centerMessageId is absent from read_messages entirely (excluded or not yet enriched),
        // both traversals return empty — return null Center so callers can distinguish "missing"
        // from "isolated message with no conversation context".
        if (ancestors.Count == 0 && descendants.Count == 0)
            return new ConversationCluster(null, [], []);

        var center = ancestors.Count > 0
            ? ancestors[^1]
            : descendants[0]; // depth=0 is always the root of the thread traversal

        // Exclude the center from both lists — caller wants ancestors above and descendants below.
        var ancestorsWithoutCenter = ancestors.Count > 1
            ? ancestors.Take(ancestors.Count - 1).ToList()
            : (IReadOnlyList<ConversationNode>)[];

        var descendantsWithoutCenter = descendants.Count > 1
            ? descendants.Skip(1).ToList()
            : (IReadOnlyList<ConversationNode>)[];

        return new ConversationCluster(center, ancestorsWithoutCenter, descendantsWithoutCenter);
    }

    private static async Task<IReadOnlyList<ConversationNode>> ReadNodesAsync(
        NpgsqlCommand cmd,
        CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<ConversationNode>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ConversationNode(
                MessageId:  reader.GetInt64(0),
                ReplyToId:  reader.IsDBNull(1) ? null : reader.GetInt64(1),
                ChannelId:  reader.GetInt64(2),
                GuildId:    reader.GetInt64(3),
                AuthorId:   reader.GetInt64(4),
                CreatedAt:  reader.GetFieldValue<DateTimeOffset>(5),
                Depth:      reader.GetInt32(6)));
        }

        return results;
    }
}
