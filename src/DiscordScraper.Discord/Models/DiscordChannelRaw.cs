namespace DiscordScraper.Discord.Models;

/// <summary>
/// Either a guild channel or a thread — threads are modeled as channels of
/// type 10/11/12 in Discord's API.
/// </summary>
/// <param name="ChannelId">Snowflake of the channel or thread.</param>
/// <param name="GuildId">Parent guild; not always present on thread responses,
/// set by the caller from context.</param>
/// <param name="Type">Discord channel type. 0/5/15 are text-like channels;
/// 10/11/12 are threads. 2/4/13/14 are voice/category/stage/directory and
/// should be skipped by the sync worker.</param>
/// <param name="ParentId">For threads, the containing channel snowflake. For
/// regular channels, null (or the category id, which we ignore).</param>
/// <param name="Name">Human-readable channel name.</param>
/// <param name="Payload">Raw JSON of the channel object for <c>raw_channels.payload</c>.</param>
public sealed record DiscordChannelRaw(
    long ChannelId,
    long GuildId,
    int Type,
    long? ParentId,
    string Name,
    string Payload);
