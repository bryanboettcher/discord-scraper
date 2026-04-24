namespace DiscordScraper.Discord.Models;

/// <summary>
/// A Discord message as returned by the REST API, with the fields needed for
/// Tier 1 storage routing decoded once and the original JSON preserved verbatim
/// in <see cref="Payload"/>.
/// </summary>
/// <param name="MessageId">Snowflake parsed from the Discord <c>id</c> string.</param>
/// <param name="ChannelId">Channel the message belongs to. For thread messages this
/// is the thread's snowflake, not the parent channel.</param>
/// <param name="GuildId">Set by the caller; Discord's message payload does not
/// carry this for channel-scoped fetches.</param>
/// <param name="CreatedAt">Timestamp decoded from the message snowflake.</param>
/// <param name="EditedAt">Last-edit timestamp parsed from the payload's
/// <c>edited_timestamp</c> field, or null if the message has never been edited.
/// Used by the pin poller to append rows to <c>raw_message_edits</c> when a
/// pinned message's content changes.</param>
/// <param name="Payload">Raw JSON text of the message object, exactly as Discord
/// sent it. Stored verbatim in <c>raw_messages.payload</c>.</param>
public sealed record DiscordMessageRaw(
    long MessageId,
    long ChannelId,
    long GuildId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    string Payload);
