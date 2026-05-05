namespace DiscordScraper.Api.Admin;

public sealed record SagaCountsByState(
    IReadOnlyDictionary<string, long> Guild,
    IReadOnlyDictionary<string, long> Channel,
    IReadOnlyDictionary<string, long> Message);

public sealed record GuildSagaSnapshot(
    long GuildId,
    string Name,
    string CurrentState,
    DateTimeOffset? UpdatedOn,
    DateTimeOffset LastSyncedAt,
    int LastSyncChannelCount,
    int RoleCount);

public sealed record ChannelSagaSnapshot(
    long ChannelId,
    long GuildId,
    string Name,
    int ChannelType,
    string CurrentState,
    long LastSyncedSnowflake,
    DateTimeOffset LastSyncedAt,
    bool IsCaughtUpAtLastPoll,
    int LastSyncMessageCount,
    string? PinSetCanonical);

public sealed record MessageSagaSnapshot(
    long MessageSnowflake,
    long ChannelId,
    long GuildId,
    long AuthorId,
    bool AuthorIsBot,
    string CurrentState,
    DateTimeOffset? UpdatedOn,
    DateTimeOffset MessageCreatedAt,
    DateTimeOffset? EditedTimestamp,
    bool HasPendingEdit,
    bool? IsSubstantive,
    bool? IsBot,
    string? DetectedLanguage,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? IndexedAt);

public sealed record ReadStoreCounts(
    long Messages,
    long Channels,
    long Guilds,
    long Vectors);

public sealed record AdminStatsResponse(
    ReadStoreCounts ReadStore,
    SagaCountsByState Sagas,
    DateTimeOffset GeneratedAt);

public sealed record SyncStatusResponse(
    SagaCountsByState Counts,
    int RecentlySyncedChannels,
    DateTimeOffset GeneratedAt);
