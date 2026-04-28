namespace DiscordScraper.Read.Graph;

/// <summary>
/// Fixed-shape SQL constants for conversation graph traversal.
/// Extracted from PgConversationGraph so query strings are independently testable
/// without constructing the full implementation or touching Npgsql.
/// </summary>
internal static class ConversationGraphSqlBuilder
{
    /// <summary>
    /// Descendants via forward recursive CTE. Traverses reply_to_id chains downward from a root.
    /// depth bound in the recursive term prevents runaway on corrupt data; Discord reply semantics
    /// guarantee no natural cycles (replies always target older messages), but we don't trust that.
    /// </summary>
    internal const string GetThreadSql = """
        WITH RECURSIVE thread (message_id, reply_to_id, channel_id, guild_id, author_id, created_at, depth) AS (
            SELECT message_id, reply_to_id, channel_id, guild_id, author_id, created_at, 0
            FROM read_messages
            WHERE message_id = @root_id
          UNION ALL
            SELECT m.message_id, m.reply_to_id, m.channel_id, m.guild_id, m.author_id, m.created_at, t.depth + 1
            FROM read_messages m
            JOIN thread t ON m.reply_to_id = t.message_id
            WHERE t.depth < @max_depth
        )
        SELECT message_id, reply_to_id, channel_id, guild_id, author_id, created_at, depth
        FROM thread
        ORDER BY created_at;
        """;

    /// <summary>
    /// Ancestors via reverse recursive CTE. Traverses reply_to_id chains upward toward the root.
    /// Result is ordered depth DESC so callers see root first, leaf last (oldest-first in conversation order).
    /// </summary>
    internal const string GetReplyAncestorsSql = """
        WITH RECURSIVE ancestors (message_id, reply_to_id, channel_id, guild_id, author_id, created_at, depth) AS (
            SELECT message_id, reply_to_id, channel_id, guild_id, author_id, created_at, 0
            FROM read_messages
            WHERE message_id = @leaf_id
          UNION ALL
            SELECT m.message_id, m.reply_to_id, m.channel_id, m.guild_id, m.author_id, m.created_at, a.depth + 1
            FROM read_messages m
            JOIN ancestors a ON a.reply_to_id = m.message_id
            WHERE a.depth < @max_depth
        )
        SELECT message_id, reply_to_id, channel_id, guild_id, author_id, created_at, depth
        FROM ancestors
        ORDER BY depth DESC;
        """;
}
