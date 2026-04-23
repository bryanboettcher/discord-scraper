namespace DiscordScraper.Ingestion.Projection;

/// <summary>
/// Per-pass, read-only caches the projector consults when resolving mentions
/// and detecting threads. Built once at the start of a projection pass so each
/// message resolution is an in-memory dictionary lookup, not a DB round-trip.
/// </summary>
/// <param name="Channels">All currently-known channels keyed by channel id.
/// Used for both <c>&lt;#id&gt;</c> mention resolution and thread type/parent
/// detection.</param>
/// <param name="GuildRoles">Per-guild role id → role name lookup, built from
/// the latest <c>raw_guilds</c> snapshot's <c>roles</c> array.</param>
public sealed record ProjectionContext(
    IReadOnlyDictionary<long, ChannelInfo> Channels,
    IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>> GuildRoles);

/// <summary>
/// Minimal shape the projector needs from a channel snapshot: enough to detect
/// thread hierarchy and resolve <c>&lt;#id&gt;</c> mentions to a display name.
/// </summary>
public sealed record ChannelInfo(long ChannelId, int Type, long? ParentId, string Name);
