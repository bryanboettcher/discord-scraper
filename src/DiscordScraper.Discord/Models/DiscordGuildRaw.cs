namespace DiscordScraper.Discord.Models;

/// <param name="GuildId">Snowflake parsed from the Discord <c>id</c> string.</param>
/// <param name="Name">Human-readable guild name; useful for startup logging.</param>
/// <param name="Payload">Raw JSON of the guild object for <c>raw_guilds.payload</c>.</param>
public sealed record DiscordGuildRaw(
    long GuildId,
    string Name,
    string Payload);
