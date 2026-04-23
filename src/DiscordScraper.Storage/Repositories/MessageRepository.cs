using DiscordScraper.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DiscordScraper.Storage.Repositories;

internal sealed class MessageRepository(IDbContextFactory<DiscordScraperDbContext> contextFactory) : IMessageRepository
{
    // Parallel-array unnest keeps the batch to a single round-trip. content_tsv
    // is generated/stored by Postgres and is intentionally omitted from both
    // the insert list and the update list. created_at is immutable (derived
    // from the message snowflake) so it's excluded from the update set too.
    private const string UpsertSql = """
        INSERT INTO messages (
            message_id, channel_id, guild_id, author_id, author_name, author_is_bot,
            content, created_at, edited_at, reply_to_id, thread_id, root_channel_id,
            has_attachments
        )
        SELECT * FROM unnest(
            @message_ids, @channel_ids, @guild_ids, @author_ids, @author_names, @author_is_bots,
            @contents, @created_ats, @edited_ats, @reply_to_ids, @thread_ids, @root_channel_ids,
            @has_attachments
        )
        ON CONFLICT (message_id) DO UPDATE SET
            channel_id      = EXCLUDED.channel_id,
            guild_id        = EXCLUDED.guild_id,
            author_id       = EXCLUDED.author_id,
            author_name     = EXCLUDED.author_name,
            author_is_bot   = EXCLUDED.author_is_bot,
            content         = EXCLUDED.content,
            edited_at       = EXCLUDED.edited_at,
            reply_to_id     = EXCLUDED.reply_to_id,
            thread_id       = EXCLUDED.thread_id,
            root_channel_id = EXCLUDED.root_channel_id,
            has_attachments = EXCLUDED.has_attachments
        """;

    public async Task<int> UpsertBatchAsync(IReadOnlyList<MessageEntity> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return 0;

        var count = messages.Count;
        var messageIds = new long[count];
        var channelIds = new long[count];
        var guildIds = new long[count];
        var authorIds = new long[count];
        var authorNames = new string[count];
        var authorIsBots = new bool[count];
        var contents = new string[count];
        var createdAts = new DateTimeOffset[count];
        var editedAts = new DateTimeOffset?[count];
        var replyToIds = new long?[count];
        var threadIds = new long?[count];
        var rootChannelIds = new long[count];
        var hasAttachments = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var m = messages[i];
            messageIds[i] = m.MessageId;
            channelIds[i] = m.ChannelId;
            guildIds[i] = m.GuildId;
            authorIds[i] = m.AuthorId;
            authorNames[i] = m.AuthorName;
            authorIsBots[i] = m.AuthorIsBot;
            contents[i] = m.Content;
            createdAts[i] = m.CreatedAt;
            editedAts[i] = m.EditedAt;
            replyToIds[i] = m.ReplyToId;
            threadIds[i] = m.ThreadId;
            rootChannelIds[i] = m.RootChannelId;
            hasAttachments[i] = m.HasAttachments;
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        return await context.Database.ExecuteSqlRawAsync(
            UpsertSql,
            parameters: new object[]
            {
                new NpgsqlParameter("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = messageIds },
                new NpgsqlParameter("channel_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = channelIds },
                new NpgsqlParameter("guild_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = guildIds },
                new NpgsqlParameter("author_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = authorIds },
                new NpgsqlParameter("author_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = authorNames },
                new NpgsqlParameter("author_is_bots", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = authorIsBots },
                new NpgsqlParameter("contents", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = contents },
                new NpgsqlParameter("created_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = createdAts },
                new NpgsqlParameter("edited_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = editedAts },
                new NpgsqlParameter("reply_to_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = replyToIds },
                new NpgsqlParameter("thread_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = threadIds },
                new NpgsqlParameter("root_channel_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rootChannelIds },
                new NpgsqlParameter("has_attachments", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = hasAttachments },
            },
            cancellationToken: ct);
    }
}
