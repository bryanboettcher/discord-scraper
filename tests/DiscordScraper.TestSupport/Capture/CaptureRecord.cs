using DiscordScraper.Contracts.Events.Guild;
using System.Text.Json;

namespace DiscordScraper.TestSupport.Capture;

// ---------------------------------------------------------------------------
// JSONL line shapes.  "kind" discriminator is on the outer envelope; "data"
// is the body object as captured by MessageCaptureConsumer.
// ---------------------------------------------------------------------------

/// <summary>First line of every fixture file.</summary>
public sealed record CaptureHeader(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    string SourceGuild,
    string ScraperSha)
{
    /// <summary>Schema version this loader understands. Reject files whose version is higher.</summary>
    public const int SupportedVersion = 1;
}

/// <summary>Outer wrapper for every non-header JSONL line.</summary>
public sealed record CaptureEnvelope(string Kind, JsonElement Data);

// ---------------------------------------------------------------------------
// Concrete body DTOs for each captured fact. Used only for deserialization and
// republication; they do not need to implement the contract interfaces — MT
// maps properties by name when publishing via IBus.Publish<TInterface>(body).
// Property names must match the camelCase-serialized output of the capture consumer.
// ---------------------------------------------------------------------------

public sealed record MessageCapturedBody(
    Guid MessageId,
    long MessageSnowflake,
    long ChannelId,
    long GuildId,
    long AuthorId,
    string CurrentState,
    DateTimeOffset UpdatedOn,
    string PayloadJson,
    bool AuthorIsBot,
    string? HomeChannelName);

public sealed record MessageEditObservedBody(
    Guid MessageId,
    long MessageSnowflake,
    long ChannelId,
    long GuildId,
    long AuthorId,
    string CurrentState,
    DateTimeOffset UpdatedOn,
    DateTimeOffset EditedAt,
    string UpdatedPayloadJson);

public sealed record ChannelChangedBody(
    Guid CorrelationId,
    long ChannelId,
    long GuildId,
    string CurrentState,
    DateTimeOffset UpdatedOn,
    string Name,
    string? Topic,
    int ChannelType,
    long? ParentId,
    bool IsPresent);

public sealed record GuildChangedBody(
    Guid CorrelationId,
    long GuildId,
    string CurrentState,
    DateTimeOffset UpdatedOn,
    string Name,
    IReadOnlyList<GuildRole> Roles,
    bool IsPresent);
