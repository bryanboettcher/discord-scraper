using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events;
using DiscordScraper.Contracts.IR;
using MassTransit;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Write-side canonical message entity. Persists indefinitely — no terminal state.
/// CorrelationId == MessageId == DeterministicGuid.FromSnowflake(MessageSnowflake) so any
/// node receiving a snowflake computes the same saga key without coordination.
///
/// MessageId and CorrelationId are kept as separate fields because Discord REST calls key on
/// the snowflake while MT saga correlation requires a Guid. Having both avoids the per-call
/// DeterministicGuid.FromSnowflake() round-trip inside consumers.
/// </summary>
public sealed class MessageSagaState : SagaStateMachineInstance, ISagaVersion, ITimestamped, MessageModelBase
{
    // MT Mongo repo requires parameterless ctor; all init done by the state machine.
    public MessageSagaState() { }

    public Guid CorrelationId { get; set; }

    /// <summary>MT optimistic concurrency on FindOneAndReplace.</summary>
    public int Version { get; set; }

    /// <summary>InstanceState backing field; bound via <c>InstanceState(x =&gt; x.CurrentState)</c>.</summary>
    public string CurrentState { get; set; } = string.Empty;

    // --- Identity ---

    /// <summary>Deterministic Guid derived from MessageSnowflake. Equals CorrelationId.</summary>
    public Guid MessageId { get; set; }

    /// <summary>Raw Discord message snowflake. Use for Discord API calls and read-model joins.</summary>
    public long MessageSnowflake { get; set; }

    public long ChannelId { get; set; }
    public long GuildId { get; set; }
    public long AuthorId { get; set; }
    public bool AuthorIsBot { get; set; }

    public DateTimeOffset LastUpdatedAt { get; set; }

    /// <summary>
    /// Verbatim Discord JSON captured at ingestion time. Held so the projector can rebuild the
    /// IR from the saga without re-fetching Discord REST.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    // --- AnalyzeMessage response ---
    public bool? IsSubstantive { get; set; }
    public bool? IsBot { get; set; }
    public string? DetectedLanguage { get; set; }

    /// <summary>
    /// Typed AST stored as a BSON sub-document. Populated by ProjectMessage.Completed.
    /// MongoBsonRegistration.RegisterAll() must run before any saga is persisted to ensure
    /// the polymorphic MessageNode hierarchy has discriminators configured.
    /// </summary>
    public MessageIR? IR { get; set; }

    // --- Enhancement output ---
    public IReadOnlyList<string>? Tags { get; set; }
    public float[]? Embedding { get; set; }
    public DateTimeOffset? IndexedAt { get; set; }

    /// <summary>
    /// Model version used for the most recent embedding. Stamped from EnhanceMessageResponse.
    /// ReEmbeddingRequested matches sagas where this differs from the requested ModelVersion.
    /// </summary>
    public string? EmbeddingModelVersion { get; set; }

    /// <summary>
    /// Model version used for the most recent tagging pass. Stamped from EnhanceMessageResponse.
    /// ReTagRequested matches sagas where this differs from the requested ModelVersion.
    /// </summary>
    public string? TagModelVersion { get; set; }

    // --- Edit tracking ---

    /// <summary>EditedTimestamp from the most recent MessageEditObserved event.</summary>
    public DateTimeOffset? EditedTimestamp { get; set; }

    /// <summary>
    /// Set to true when MessageEditObserved arrives while a request is in flight. Each
    /// *.Completed handler checks this flag and re-loops into Request(ProjectMessage) if set,
    /// ensuring no edit is silently dropped.
    /// </summary>
    public bool HasPendingEdit { get; set; }

    // --- Request correlation IDs (MT requires Guid? per Request declaration) ---
    public Guid? AnalyzeMessageRequestId { get; set; }
    public Guid? ProjectMessageRequestId { get; set; }
    public Guid? EnhanceMessageRequestId { get; set; }
    public Guid? IndexMessageRequestId { get; set; }

    /// <summary>
    /// UTC creation time decoded from the snowflake on capture. Forwarded to
    /// IndexMessageRequest.CreatedAt so the vector point carries the original message timestamp
    /// without an extra round-trip.
    /// </summary>
    public DateTimeOffset MessageCreatedAt { get; set; }
}
