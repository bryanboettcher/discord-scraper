# Architecture Plan — CQRS + MassTransit Rewrite

**Status:** In planning. No code written for this rewrite. Working tree at commit `03c13b1` on `initial` branch with the previous architecture (three BackgroundServices coordinating through Postgres polling) intact and live-verified (1137 messages captured, projected, partially enriched; pin capture + edit detection working). Previous architecture stays untouched until rewrite is staged for replacement.

**Schema policy for v1:** No EF migrations or Mongo schema versioning during initial build. Models on disk are v1; drop and recreate as needed. Migrations begin once we have data worth preserving.

## Context and Goals

- **Owner framing.** Hobby/learning project. Real goal is a GitHub repo demonstrating architectural sophistication for hyperscale-company interview contexts. Pattern fluency across CQRS, sagas-as-entity-projections, event-driven projections, polyglot persistence at the right boundaries.
- **Functional target.** Multi-guild Discord ingester capable of handling a 30k-member server. Today's test server is ~1k messages; target is 500k–5M with live ingestion.
- **Hard commitment.** Wholesale rewrite from polling coordination to event-driven CQRS with MassTransit. No hybrid phase.
- **Scale corridor.** Defensible at 5M, believable at 500M, articulable at 50B (where each component would change but the seams stay).
- **Channel scope.** Text channels only for v1. Forum/voice/announcement/stage channel handling deferred.

## Storage Architecture

**Polyglot at the system level, single-engine where possible.**

- **MongoDB** — write-side saga store. MT Mongo saga repository for `MessageSaga`, `ChannelSaga`, `GuildSaga`. Native BSON for typed saga state including the message IR. Single-node replica set required for MT outbox transactions. Outbox is Mongo (transactional with saga writes). Sharding-native at scale.
- **PostgreSQL with extensions** — read-side everything. Single Postgres instance, multiple paradigms via extensions:
  - **TimescaleDB** — hypertables for time-shaped tables. "Postgres" in this document means "Postgres + TimescaleDB" unless noted.
  - **pgvector** — vector embeddings, behind `IVectorStore`.
  - **Native TSV** — full-text search, behind `ISearchService`.
  - **jsonb** — IR storage and structured fields.
  - **Apache AGE** — graph queries (deferred; recursive CTEs cover today's needs behind `IConversationGraph`).
- **Ollama** — local LLM service for embedding + tagging. Unchanged.
- **RabbitMQ** — single-node MT transport. Chosen for management UI. `rabbitmq_delayed_message_exchange` plugin required (request-timeout scheduler + ChannelSaga heartbeats).

**Stage-2 migration corridors:** pgvector → standalone Qdrant; TSV → Tantivy/Meilisearch/Elasticsearch; recursive CTE → AGE → Neo4j; Postgres OLAP → ClickHouse alongside.

Each corridor is a single-implementation change behind its interface.

## Saga Design

**Sagas are entity projections.** Saga state is the canonical write-side representation. Saga IS the entity (MessageSaga IS the message; ChannelSaga IS the channel; GuildSaga IS the guild). Not a workflow-table-next-to-an-entity-table.

**Sagas persist indefinitely.** No "completed and deleted" terminal state. A message saga can receive `MessageEditObserved` events years after enrichment; a channel saga monitors a channel for as long as the channel exists on the server. Mongo saga repo handles 5M+ persisting instances trivially — optimistic FindOneAndReplace per instance, no cross-instance locking. Long-lived sagas with insert-once + update-by-PK access patterns are exactly the workload Mongo handles best.

**Saga state carries lifecycle facts only** — no denormalized cross-entity context. Justification: projection produces IR (typed AST), not rendered text. References in the IR are typed IDs with snapshot fallback strings captured at projection time. Display rendering is a read-side concern that walks IR against current entity state. Without saga-side denormalization, the cascade of gaps (stale projections, MessageCaptured/ChannelChanged ordering hazards, synthetic-publish schema migrations, fault storms) doesn't exist.

**MessageSagaState carries:**
- Identity: MessageId (correlation = `DeterministicGuid.FromSnowflake`), ChannelId, GuildId, AuthorId.
- Lifecycle: CurrentState (string), Version, LastUpdatedAt.
- Raw payload: PayloadJson (verbatim Discord message JSON for re-projection).
- Analysis: IsSubstantive, IsBot, DetectedLanguage (from MessageEnhancement Analyze step).
- Projection output: IR (typed AST as nested BSON document).
- Enrichment output: Tags, Embedding, IndexedAt.
- Edit tracking: EditedTimestamp, HasPendingEdit (gates re-projection during in-flight requests).

**ChannelSaga has no terminal state.** A channel that exists on Discord is monitored forever. State tracks progress, not completion:
- LastSyncedSnowflake (cursor for next pagination).
- LastSyncedAt.
- IsCaughtUpAtLastPoll (boolean; transient observation, not terminal).
- PinSetCanonical (for pin-change detection).
- IsPresent (false if removed from guild; archive after grace period rather than delete).

ChannelSaga does NOT track ExpectedMessages/CompletedMessages counters. Backfill "completion" is implicit in cursor advancement: when a paginated response is short or empty, IsCaughtUpAtLastPoll flips true. Sync work is event-driven from then on (timers, ChannelChanged, pin polls).

## Saga Orchestration via Request/Response

Saga workflow uses MT's `Request<TRequest, TResponse>` pattern for consumer calls. Pub/sub events are reserved for cross-saga and read-model concerns.

**Request/response inside the saga:**
- Saga declares `public Request<MessageSagaState, AnalyzeMessageRequest, AnalyzeMessageResponse> AnalyzeMessage { get; private set; }`.
- MT auto-creates `AnalyzeMessage.Pending` state.
- `.Request(AnalyzeMessage, ctx => ctx.Init<AnalyzeMessageRequest>(...)).TransitionTo(AnalyzeMessage.Pending)` issues the request and parks the saga.
- Consumer receives `IConsumer<AnalyzeMessageRequest>`, does work, calls `context.RespondAsync<AnalyzeMessageResponse>(...)`.
- `During(AnalyzeMessage.Pending, When(AnalyzeMessage.Completed).Then(...).TransitionTo(...))` resumes the saga.

Request timeout requires a scheduler — `UseDelayedMessageScheduler()` against the RabbitMQ delayed-exchange plugin. Default per-request timeout is 30 seconds; tune per request type. `AnalyzeMessage.Faulted` and `AnalyzeMessage.TimeoutExpired` events also need handlers (transition to a `Faulted` state, log, escalate via DLQ).

**Pub/sub events:**
- After each major state transition, saga publishes a state-changed event (e.g., `MessageProjected`, `MessageEnriched`).
- Read-side `Batch<MessageStateChanged>` consumers subscribe to populate `read_messages` and extraction tables.
- Pub/sub is for fan-out to N independent subscribers; request/response is for one-saga-orchestrating-one-consumer steps.

**Composite request consumers eliminate fan-in OCC collisions.** When two parallel responses both want to update the saga, they race on optimistic concurrency. Solution: one Request from saga, the consumer does parallel work internally and returns a combined response.

`MessageEnhancementConsumer` is the canonical example: receives `EnhanceMessageRequest`, does Tag (Ollama call) + Embed (Ollama call) — internally parallel via `Task.WhenAll` — returns `EnhanceMessageResponse { Tags, Embedding }`. Saga sees one round-trip; consumer handles the parallelism.

## State Machine Pattern

**State flow** for `MessageSaga`:
```
[initial] --MessageCaptured-->
  ApplyState, Request(AnalyzeMessage)
  --> AnalyzeMessage.Pending
       --AnalyzeMessage.Completed && IsSubstantive--> Request(ProjectMessage) --> ProjectMessage.Pending
       --AnalyzeMessage.Completed && !IsSubstantive--> Excluded
       --AnalyzeMessage.Faulted--> Faulted
  
  ProjectMessage.Pending
       --ProjectMessage.Completed--> StoreIR, Request(EnhanceMessage) --> EnhanceMessage.Pending
       --ProjectMessage.Faulted--> Faulted

  EnhanceMessage.Pending
       --EnhanceMessage.Completed--> StoreTags+Embedding, Request(IndexMessage) --> IndexMessage.Pending
       --EnhanceMessage.Faulted--> Faulted

  IndexMessage.Pending
       --IndexMessage.Completed--> StoreIndexed --> Enriched
       --IndexMessage.Faulted--> Faulted

  Enriched (steady state, persists indefinitely)
       --MessageEditObserved--> SetHasPendingEdit, Request(ProjectMessage) --> ProjectMessage.Pending (reuse)
       --MessageDeleted--> SoftDeleted

  ProjectMessage.Pending / EnhanceMessage.Pending / IndexMessage.Pending
       --MessageEditObserved--> SetHasPendingEdit (queue; on next *.Completed handler, check flag and re-loop)
```

**Multi-state `During` for shared event handling:**
```csharp
During(new[] { AnalyzeMessage.Pending, ProjectMessage.Pending, EnhanceMessage.Pending, IndexMessage.Pending },
    When(MessageEditObserved)
        .Then(ctx => ctx.Saga.HasPendingEdit = true));
```

The flag pattern is essential — without it, edits arriving during an in-flight request get dropped. On each `*.Completed` handler, check `HasPendingEdit` and re-loop into `Request(ProjectMessage, ...)` if set.

## State Machine Helpers

To reduce per-event boilerplate, introduce extension-method helpers.

**Common timestamp + state mutation:**
```csharp
public static class StateMachineExtensions
{
    public static EventActivityBinder<TSaga, TEvent> ApplyUpdate<TSaga, TEvent>(
        this EventActivityBinder<TSaga, TEvent> binder,
        ISystemClock clock,
        Action<TSaga, TEvent> applyEvent = null)
        where TSaga : class, SagaStateMachineInstance, ITimestamped
        where TEvent : class
    {
        return binder.Then(ctx =>
        {
            ctx.Saga.LastUpdatedAt = clock.UtcNow;
            applyEvent?.Invoke(ctx.Saga, ctx.Message);
        });
    }
}
```

**Saga state as event payload (no helper needed).** When saga implements `MessageModelBase` and an event extends `MessageModelBase`, MT's anonymous-publish trick lets you publish saga state directly:
```csharp
.PublishAsync(ctx => ctx.Init<MessageProjected>(ctx.Saga))
```
The saga satisfies the event's interface; MT serializes it. No mapping helper needed for the common case.

**Composite update + publish helper for the common pattern:**
```csharp
public static EventActivityBinder<TSaga, TEvent> ApplyAndAnnounce<TSaga, TEvent, TPublish>(
    this EventActivityBinder<TSaga, TEvent> binder,
    ISystemClock clock,
    Action<TSaga, TEvent> applyEvent = null)
    where TSaga : class, SagaStateMachineInstance, ITimestamped, MessageModelBase
    where TEvent : class
    where TPublish : class, BaseMessageEvent
{
    return binder
        .ApplyUpdate(clock, applyEvent)
        .PublishAsync(ctx => ctx.Init<TPublish>(ctx.Saga));
}
```

DI dependencies (`ISystemClock`, repos) get captured by closure when the state machine is instantiated.

## Event Hierarchy and Naming Conventions

Per-domain `*ModelBase` interfaces. **No `I` prefix** — these are MT message contracts; the interface-ness is incidental. Add `// ReSharper disable InconsistentNaming` per file.

```csharp
public interface MessageModelBase : CorrelatedBy<Guid>
{
    Guid MessageId { get; }
    long ChannelId { get; }
    long GuildId { get; }
    long AuthorId { get; }
    string CurrentState { get; }
    new Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageId);
}
```

Events extend the model, depth ≤3 levels:
```
MessageModelBase
  └─ BaseMessageEvent                        [ExcludeFromTopology, ExcludeFromImplementedTypes]
       ├─ MessageCaptured
       ├─ MessageStateChanged                [ExcludeFromTopology, ExcludeFromImplementedTypes]
       │    ├─ MessageProjected
       │    ├─ MessageEnhanced
       │    ├─ MessageIndexed
       │    └─ MessageEnriched
       ├─ MessageEditObserved
       └─ MessageDeleted
```

`[ExcludeFromTopology, ExcludeFromImplementedTypes]` on intermediate base interfaces prevents MT from creating queues for the abstract layers.

State is `string CurrentState`, not `int`. Ordinal-free; states can be added/reordered without migration.

`ISystemClock` injected into state machines. `DeterministicGuid.FromSnowflake(messageId)` for correlation.

## The Message IR

Typed AST of a Discord message. Captures structure with typed references but NOT rendered text. Renderer walks IR + current entity state at read time.

```csharp
public abstract record MessageNode;
public sealed record TextNode(string Text) : MessageNode;
public sealed record MentionNode(MentionKind Kind, long? Id, string Fallback) : MessageNode;
public sealed record ChannelRefNode(long ChannelId, string Fallback) : MessageNode;
public sealed record EmojiNode(EmojiKind Kind, long? Id, string NameOrGlyph, bool Animated) : MessageNode;
public sealed record TimestampNode(DateTimeOffset Value, TimestampStyle Style) : MessageNode;
public sealed record FormattingNode(FormattingKind Kind, IReadOnlyList<MessageNode> Children) : MessageNode;
public sealed record CodeBlockNode(string? Language, string Content) : MessageNode;
public sealed record InlineCodeNode(string Content) : MessageNode;
public sealed record QuoteNode(IReadOnlyList<MessageNode> Children) : MessageNode;
public sealed record LinkNode(string Url, IReadOnlyList<MessageNode> DisplayChildren) : MessageNode;

public sealed record MessageIR(
    IReadOnlyList<MessageNode> Body,
    IReadOnlyList<AttachmentIR> Attachments,
    IReadOnlyList<EmbedIR> Embeds,
    ReplyContext? ReplyTo,
    DateTimeOffset CapturedAt);
```

**Storage:** typed BSON sub-document on saga side; `jsonb` column on read side.

**Rendering** is a pure function, read-side:
```csharp
public static string Render(MessageIR ir, RenderContext ctx);
public sealed record RenderContext(
    IReadOnlyDictionary<long, string> ChannelNames,
    IReadOnlyDictionary<long, string> RoleNames,
    IReadOnlyDictionary<long, string> UserNames,
    IReadOnlyDictionary<long, string> EmojiNames);
```

Walks the tree, resolves references via context; falls back to captured `Fallback` strings for unresolvable references. Pure C#; context loaded once per query.

**Fallback sources at projection time:** user mentions from Discord payload's `mentions[]`; channel/role names from direct repository lookups (see Projection Context below); custom emoji name+id from message reference syntax.

## Projection Context (Direct Lookup Pattern)

`ProjectMessageConsumer` parses the message into a preliminary IR collecting ChannelIds and RoleIds that need fallback names, then fetches them directly from the saga stores via `Task.WhenAll`:

```csharp
public async Task Consume(ConsumeContext<ProjectMessageRequest> context)
{
    var preliminary = MessageParser.ParseStructure(context.Message.PayloadJson);

    var (channelNames, roleNames) = await TaskEx.WhenAll(
        _channelRepo.GetNamesAsync(preliminary.ChannelIdsNeedingNames, context.CancellationToken),
        _guildRepo.GetRoleNamesAsync(context.Message.GuildId, preliminary.RoleIdsNeedingNames, context.CancellationToken));

    var ir = MessageParser.PopulateFallbacks(preliminary, channelNames, roleNames);

    await context.RespondAsync<ProjectMessageResponse>(new { IR = ir });
}
```

**Stateless consumer.** No warmup. No drift. No separate cache hosted-service.

Channel/guild repos do indexed point-fetches against Mongo. Sub-millisecond per `$in` query. Two queries per projection × Σ(1–2 references per message) — negligible at projection rates this project will see.

**Caching as decorator (deferred).** If profiling later shows the lookup is hot, wrap the repo in a caching decorator that subscribes to ChannelChanged/GuildChanged events. The consumer signature stays unchanged; the cache becomes an implementation detail. Decorator pattern preserved for stage 2; v1 ships without it.

**Stamping the home channel onto MessageCaptured** — ChannelSyncConsumer already has the channel loaded during pagination; it can stamp the home-channel name onto MessageCaptured for the channel-the-message-is-in. Covers the common case where messages reference their own channel (e.g., embedded `<#thisChannel>` references). Cross-channel `<#otherChannel>` refs still need the lookup.

## MessageEnhancement Project

Replaces the Ollama-prefixed consumers from the previous design. Composite consumer that handles fast inline decisions plus 0–1 slow API calls per request type.

- **`AnalyzeMessageConsumer`** — fast inline checks: substantiveness heuristic, bot detection (`is_bot` from author), language detection. No Ollama call. Returns `AnalyzeMessageResponse` with structured analysis.
- **`EnhanceMessageConsumer`** — composite: Tag (Ollama call) + Embed (Ollama call), internally parallel via `Task.WhenAll`. Single response with both. Avoids the saga-fan-in OCC collision.
- **`IndexMessageConsumer`** — vector store write via `IVectorStore.UpsertAsync`. Returns `IndexMessageResponse { IndexedAt }`.

Naming: project is `DiscordScraper.MessageEnhancement`. Consumer names describe the function (Analyze/Enhance/Index), not the backend. Ollama dependency is internal to `EnhanceMessageConsumer`; if we later swap to a different LLM, the saga doesn't notice.

**Bot filter** — Discord exposes `author.bot` on every message. `MessageCaptured` carries the flag (filtered at `ChannelSyncConsumer` if we want to refuse ingestion of bot messages entirely, or carried through to AnalyzeMessage if we want to capture but not enrich). For v1, capture all and let AnalyzeMessage's `IsBot` flag drive downstream gating; saga branches to Excluded if substantiveness or bot rules say "skip enrichment."

## Read-Side Architecture

Normalized read tables with extracted projection tables for queryable structure. jsonb for the IR.

**Schema decisions:**
- `read_messages` is a TimescaleDB hypertable partitioned by `created_at` (1-week chunks).
- Composite uniqueness `(message_id, created_at)` is implicit (Discord snowflakes are globally unique); we do NOT enforce a DB-level PK that includes `created_at`. We accept that DB-level uniqueness on `message_id` is logical-only, not enforced. Idempotent-insert discipline at the consumer level handles duplicate prevention.
- No FK constraints from extraction tables back to `read_messages`. Logical-only. Orphan cleanup via periodic job if it ever becomes a concern.
- Consequence: DDL is simpler and TimescaleDB's hypertable semantics work cleanly.

```sql
CREATE TABLE read_messages (
    message_id      BIGINT NOT NULL,
    channel_id      BIGINT NOT NULL,
    guild_id        BIGINT NOT NULL,
    author_id       BIGINT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL,
    edited_at       TIMESTAMPTZ,
    reply_to_id     BIGINT,
    ir              JSONB NOT NULL,
    plain_text      TEXT NOT NULL,
    tsv             TSVECTOR GENERATED ALWAYS AS (to_tsvector('english', plain_text)) STORED,
    has_code        BOOLEAN NOT NULL,
    has_attachments BOOLEAN NOT NULL,
    has_embeds      BOOLEAN NOT NULL,
    is_substantive  BOOLEAN NOT NULL,
    is_bot          BOOLEAN NOT NULL
);
SELECT create_hypertable('read_messages', 'created_at', chunk_time_interval => INTERVAL '7 days');

-- Non-unique indexes on the hypertable
CREATE INDEX read_messages_message_id_idx ON read_messages (message_id);
CREATE INDEX read_messages_channel_created_idx ON read_messages (channel_id, created_at DESC);
CREATE INDEX read_messages_author_idx ON read_messages (author_id);
CREATE INDEX read_messages_tsv_idx ON read_messages USING GIN (tsv);

ALTER TABLE read_messages SET (timescaledb.compress, timescaledb.compress_segmentby = 'channel_id');
SELECT add_compression_policy('read_messages', INTERVAL '30 days');

-- Extracted references (no FK; logical relation only)
CREATE TABLE message_references (
    message_id BIGINT NOT NULL,
    kind       SMALLINT NOT NULL,
    target_id  BIGINT,
    ordinal    SMALLINT NOT NULL,
    PRIMARY KEY (message_id, ordinal)
);
CREATE INDEX message_references_target_idx ON message_references (kind, target_id);

CREATE TABLE message_attachments (
    message_id    BIGINT NOT NULL,
    attachment_id BIGINT NOT NULL,
    url           TEXT NOT NULL,
    content_type  TEXT,
    size_bytes    BIGINT,
    PRIMARY KEY (message_id, attachment_id)
);

CREATE TABLE message_embeds (
    message_id   BIGINT NOT NULL,
    embed_index  SMALLINT NOT NULL,
    embed_type   TEXT,
    url          TEXT,
    title        TEXT,
    description  TEXT,
    PRIMARY KEY (message_id, embed_index)
);

CREATE TABLE message_tags (
    message_id BIGINT NOT NULL,
    tag        TEXT   NOT NULL,
    PRIMARY KEY (message_id, tag)
);
CREATE INDEX message_tags_tag_idx ON message_tags (tag);

-- Mirror tables for entity context
CREATE TABLE read_channels (
    channel_id BIGINT PRIMARY KEY,
    guild_id   BIGINT NOT NULL,
    name       TEXT NOT NULL,
    type       SMALLINT NOT NULL,
    parent_id  BIGINT,
    topic      TEXT,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE read_guilds (
    guild_id   BIGINT PRIMARY KEY,
    name       TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

-- Vector store via pgvector. Index choice deferred to implementation phase
-- (HNSW is the likely choice; IVFFlat needs training data we don't have).
CREATE TABLE message_vectors (
    message_id BIGINT PRIMARY KEY,
    embedding  VECTOR(768) NOT NULL
);

-- Continuous aggregate (TimescaleDB)
CREATE MATERIALIZED VIEW messages_per_channel_per_hour
WITH (timescaledb.continuous) AS
SELECT
    channel_id,
    time_bucket('1 hour', created_at) AS bucket,
    count(*) AS message_count
FROM read_messages
GROUP BY channel_id, bucket;

SELECT add_continuous_aggregate_policy('messages_per_channel_per_hour',
    start_offset => INTERVAL '7 days',
    end_offset   => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 hour');
```

**Single read-side consumer for message tables, atomic visibility.** `MessageReadConsumer` writes `read_messages` + `message_references` + `message_attachments` + `message_embeds` + `message_tags` in one EF Core SaveChanges per batch. Atomic — a query landing mid-batch sees either all or none of a message's rows.

```csharp
public abstract class ReadModelBatchConsumer<TEvent> : IConsumer<Batch<TEvent>>
    where TEvent : class
{
    protected abstract IEnumerable<object> ExtractEntities(TEvent evt);

    public async Task Consume(ConsumeContext<Batch<TEvent>> context)
    {
        await using var db = await _factory.CreateDbContextAsync(context.CancellationToken);
        foreach (var msg in context.Message)
            foreach (var entity in ExtractEntities(msg.Message))
                db.Add(entity);
        await db.BulkInsertOrUpdateAsync(/*...*/, ct: context.CancellationToken);
    }
}
```

For multi-table writes, use EF Core's tracker + `BulkInsertOrUpdateAsync` per entity type within one DbContext, all within an explicit transaction.

`Channel`/`Guild`/`Vector`/`Tag` read paths use the same base pattern with their own consumers.

**Companion `ConsumerDefinition` per consumer** for `BatchOptions`, retry, concurrency tuning. Auto-discovered via `bus.AddConsumers(assembly)`.

## Storage Abstractions

**`IVectorStore`** — pgvector today; Qdrant cluster at scale.
```csharp
public interface IVectorStore
{
    Task UpsertManyAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);
    Task<IReadOnlyList<VectorMatch>> SearchAsync(ReadOnlyMemory<float> query, VectorFilter filter, int topK, CancellationToken ct);
}
```

**`ISearchService`** — Postgres TSV today; Tantivy/Meilisearch at scale.
```csharp
public interface ISearchService
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
}
```

**`IConversationGraph`** — recursive CTE today; AGE later; Neo4j at hyperscale.
```csharp
public interface IConversationGraph
{
    Task<IReadOnlyList<MessageNode>> GetThreadAsync(long rootMessageId, int maxDepth, CancellationToken ct);
    Task<IReadOnlyList<MessageNode>> GetReplyAncestorsAsync(long messageId, int maxDepth, CancellationToken ct);
    Task<ConversationCluster> ExpandConversationAsync(long centerMessageId, int radius, CancellationToken ct);
}
```

Analytical queries and time-series operations are NOT abstracted — same query language across the migration corridor (SQL today, ClickHouse later means SQL stays SQL until extraction-time).

## Observability

OpenTelemetry instrumentation, layered:

- **`MassTransit.OpenTelemetry`** (or bus-level `.AddOpenTelemetry()` depending on version) — bus spans for publish/send/consume, saga state-transitions, retries, faults; trace context propagation across messages.
- **`OpenTelemetry.Instrumentation.Http`** — outbound HTTP calls (Discord REST, Ollama). Without this, traces stop at the consumer boundary.
- **Npgsql native OTel** via `AddNpgsql()` — Postgres queries (including pgvector) appear in trace.
- **`MongoDB.Driver.Core.Extensions.DiagnosticSources`** (community) — Mongo saga reads/writes appear in trace.
- **ILogger discipline** within Consume bodies — `LogContext.PushProperty("MessageId", ctx.Message.MessageId)` or scope-pattern at the start of each Consume to enrich domain-relevant logs with identifiers.

Net result: a publish from `ChannelSyncConsumer` → bus → `MessageSaga` initialization → `AnalyzeMessage` request → `AnalyzeMessageConsumer` → response → state transition → `ProjectMessage` request → `ProjectMessageConsumer` (Mongo lookups) → response → `EnhanceMessage` request → Ollama HTTP → response → `IndexMessage` request → pgvector insert → response → final state — all visible in one trace per message.

Add OTel libraries in Phase 1; pays off as soon as multi-step sagas are running.

## MT-Specific Optimization Knobs

- **`InsertOnInitial = true`** on MessageSaga initiator. Halves Mongo round-trips during burst insert.
- **`ConcurrentMessageLimit = 1`** per-channel ChannelSyncConsumer for serial pagination within channel.
- **`PrefetchCount = 500`** for MessageSaga endpoint during backfill.
- **`UseRateLimit(50, TimeSpan.FromSeconds(1))`** on Discord-touching endpoints (global Discord bot limit; divide by pod count when scaling out).
- **`UseDelayedMessageScheduler`** for ChannelSaga heartbeats AND request timeouts. Requires `rabbitmq_delayed_message_exchange` plugin.
- **`Batch<MessageStateChanged>`** for read consumers: MessageLimit=100, TimeLimit=5s, TimeLimitStart=FromFirst. PrefetchCount ≥ MessageLimit × ConcurrencyLimit.
- **Mongo single-node replica set** for outbox transaction support.
- **Composite request consumers** for parallel work (avoid OCC collisions on saga response handlers).
- **`HasPendingEdit` flag** on MessageSagaState to capture edits during in-flight requests.

## What Survives the Rewrite

- `DiscordScraper.Discord/` — REST client. Becomes the port that `ChannelSyncConsumer` invokes.
- `DiscordScraper.Ingestion/Ollama/` — embedding + tagging typed HttpClients. Used internally by `EnhanceMessageConsumer`.
- `DiscordScraper.Ingestion/Projection/MessageProjector.cs` — adapted to produce `MessageIR`.
- `DiscordScraper.Ingestion/Projection/ContentNormalizer.cs` — adapted to parse to typed nodes.
- `DiscordScraper.Ingestion/Enrichment/SubstantivenessFilter.cs` — used by `AnalyzeMessageConsumer`.
- `DiscordScraper.Ingestion/PayloadCanonicalization.cs` — drift detection helpers (used by ChannelSaga state transitions).
- `DiscordScraper.ServiceDefaults/` — OpenTelemetry, health checks, resilience.

## What Gets Rewritten

- `DiscordScraper.Ingestion/DiscordSyncWorker.cs` → `SyncSchedulerService` + `GuildSyncConsumer` + `ChannelSyncConsumer` + `MessageSaga`.
- `DiscordScraper.Ingestion/ProjectionWorker.cs` → `ProjectMessageConsumer` (request/response, emits IR).
- `DiscordScraper.Ingestion/EnrichmentWorker.cs` → `AnalyzeMessageConsumer` + `EnhanceMessageConsumer` + `IndexMessageConsumer` in `DiscordScraper.MessageEnhancement` project.
- `DiscordScraper.Storage/` → split:
  - **Write**: Mongo saga repositories.
  - **Read**: Postgres read-model DbContext + entities (clean v1; no migration from current schema).
- Standalone Qdrant → pgvector behind `IVectorStore`.

## New Projects

- `DiscordScraper.Contracts` — event/command interfaces, ModelBase hierarchies, MessageIR types, `DeterministicGuid`, `ITimestamped`.
- `DiscordScraper.Write` — saga state machines, request consumers (Project/Sync), MT registration.
- `DiscordScraper.MessageEnhancement` — `AnalyzeMessageConsumer`, `EnhanceMessageConsumer`, `IndexMessageConsumer`. Owns the Ollama dependency internally.
- `DiscordScraper.Read` — read DbContext, entities, Mapperly classes, batch consumers, query services, `IVectorStore`/`ISearchService`/`IConversationGraph` implementations.
- `DiscordScraper.Rendering` — `MessageRenderer`, `RenderContext`, output adapters.

## Flow Shape

```
TRIGGER
  Polling: SyncScheduler → GuildSyncRequested
  Gateway (Phase 8): DiscordGatewayConsumer → MessageCaptured directly

POLLING PATH
GuildSyncRequested
  → GuildSyncConsumer: fetch guild + channels + active threads
  → ChannelSyncRequested per channel

ChannelSyncRequested(channelId, guildId, cursor)
  → ChannelSyncConsumer (PrefetchCount=5, ConcurrentMessageLimit=1):
      • loads ChannelSaga
      • paginates Discord messages
      • for each: publishes MessageCaptured (via Mongo outbox), stamping home-channel-name
      • updates ChannelSaga (cursor advances; IsCaughtUpAtLastPoll computed from response size)

SAGA PATH (request/response orchestration)
MessageCaptured
  → MessageSaga (InsertOnInitial=true, correlation = DeterministicGuid.FromSnowflake)
      • stores raw payload + identity
      • Request(AnalyzeMessage) → AnalyzeMessage.Pending

AnalyzeMessage.Completed
  • IsSubstantive && !IsBot
      → Request(ProjectMessage) → ProjectMessage.Pending
  • else
      → Excluded (steady state; saga persists)

ProjectMessage.Completed
  • stores IR
  • Request(EnhanceMessage) → EnhanceMessage.Pending

EnhanceMessage.Completed
  • stores Tags + Embedding
  • Request(IndexMessage) → IndexMessage.Pending

IndexMessage.Completed
  • stores IndexedAt
  • TransitionTo(Enriched) [steady state, persists indefinitely]

[Edit handling]
MessageEditObserved during Enriched → Request(ProjectMessage) → ProjectMessage.Pending (re-loop)
MessageEditObserved during *.Pending → set HasPendingEdit; on next *.Completed, check flag and re-loop

[Read model path, parallel]
MessageStateChanged-hierarchy events
  → MessageReadConsumer (Batch<MessageStateChanged>, 100/5s):
      • Mapperly transforms per event
      • Single DbContext writes read_messages + message_references + message_attachments + message_embeds + message_tags
      • Atomic visibility per message
  → ChannelReadConsumer / GuildReadConsumer / VectorReadConsumer (separate batch consumers for those tables)
```

## Phased Implementation Plan

### Phase 1 — Infrastructure
- RabbitMQ single-node + delayed-exchange plugin in compose.
- MongoDB single-node replica set.
- Postgres + TimescaleDB + pgvector extensions.
- `DiscordScraper.Contracts` project with ModelBase hierarchies, `MessageIR`, `DeterministicGuid`, `ITimestamped`.
- MT DI in `Ingester/Program.cs` with Mongo outbox + delayed scheduler.
- OTel instrumentation (MT + HTTP + Npgsql + Mongo).
- DbContext factory registration.
- **Gate:** MT boots cleanly; outbox writes/drains on a test publish; OTel spans visible in dashboard.

### Phase 2 — Sync pipeline
- `SyncSchedulerService` (BackgroundService timer).
- `GuildSaga` + `GuildSyncConsumer`.
- `ChannelSaga` + `ChannelSyncConsumer` (cursor-based progress, no terminal state).
- `MessageSaga` initialization handler (Captured + Request(AnalyzeMessage)).
- Stub `AnalyzeMessageConsumer` returning `IsSubstantive=true` for now.
- Retire `DiscordSyncWorker`.
- **Gate:** live run captures messages; ChannelSaga cursor advances; sagas reach AnalyzeMessage.Pending; idempotent across restarts.

### Phase 3 — Analyze + Project + IR
- Real `AnalyzeMessageConsumer` (substantiveness, bot detection, language).
- `MessageParser` (raw payload → `MessageIR`).
- `ProjectMessageConsumer` with direct repo lookups for fallbacks.
- MessageSaga state transitions: Captured → AnalyzeMessage → ProjectMessage.Pending → ProjectMessage.Completed.
- Retire `ProjectionWorker`.
- **Gate:** sagas reach Projected with IR populated; non-substantive messages route to Excluded; deserialization tests verify IR shape.

### Phase 4 — Enhance + Index (composite consumer)
- `EnhanceMessageConsumer` (Tag + Embed via internal `Task.WhenAll`, Ollama dependency).
- `IVectorStore` pgvector implementation.
- `IndexMessageConsumer` (vector store write).
- MessageSaga: ProjectMessage.Completed → EnhanceMessage → IndexMessage → Enriched.
- Retire `EnrichmentWorker`.
- **Gate:** sagas reach Enriched; pgvector populated; vector search returns sensible results.

### Phase 5 — Pin polling + edit tracking
- ChannelSaga schedules pin polling via `Schedule<>`.
- `PinPollConsumer` emits `PinSetChanged` + `MessageEditObserved`.
- MessageSaga handles `MessageEditObserved` (re-projection re-loop with `HasPendingEdit` flag for in-flight edits).
- **Gate:** pins + edits land; re-projected messages have updated IR; flag pattern handles concurrent edits correctly.

### Phase 6 — Read models
- Postgres read schemas (clean v1; no migration).
- TimescaleDB hypertables, compression policies, continuous aggregates.
- `ReadModelBatchConsumer<>` base.
- `MessageReadConsumer` (writes message + references + attachments + embeds + tags atomically).
- `ChannelReadConsumer`, `GuildReadConsumer`, `VectorReadConsumer`.
- `MessageRenderer` + `RenderContext`.
- **Gate:** read tables match saga state; renderer produces correct output; query latency reasonable.

### Phase 7 — Storage abstractions
- `IVectorStore` (landed in Phase 4).
- `ISearchService` (Postgres TSV).
- `IConversationGraph` (recursive CTE).
- Query services for the MCP front-end.
- **Gate:** all interfaces have working implementations; basic queries return correct results.

### Phase 8 — Gateway integration (optional, separate session)
- `DiscordGatewayConsumer` (WebSocket).
- Publishes `MessageCaptured` directly from MESSAGE_CREATE.
- **Gate:** poll-sourced and gateway-sourced captures handled identically downstream.

## Settled Decisions Reference

- **Transport** → RabbitMQ single-node with delayed-exchange plugin.
- **Saga store** → MongoDB single-node replica set, MT Mongo saga repository, Mongo outbox.
- **Read-side storage** → Postgres + TimescaleDB + pgvector + native TSV. AGE deferred.
- **Saga lifecycle** → persists indefinitely; no completion/deletion at "terminal" states.
- **ChannelSaga** → no terminal state; cursor-based progress; channel-exists-on-server = monitor forever.
- **Saga orchestration** → request/response inside the state machine; pub/sub for read-model fan-out only.
- **Composite request consumers** for parallel work (no saga-level fan-in).
- **Saga state shape** → lifecycle facts only; no cross-entity denormalization; IR carries projection output.
- **Display rendering** → read-side; `MessageRenderer` walks IR against current entity state.
- **Projection context** → direct repo lookups via `Task.WhenAll`; no in-memory cache; cache as decorator deferred.
- **Schema versioning** → none for v1; models on disk are v1; drop and recreate as needed.
- **Channel scope** → text channels only for v1.

## Conventions and Working Philosophy

- **Saga-as-entity-projection** is the chosen pattern. Domain is event-accumulation, not state-mutation.
- **Saga state holds lifecycle facts only.** Projection emits IR; read side does denormalization and rendering.
- **IR (typed AST) is the artifact** that decouples structural extraction from display rendering.
- **Storage abstractions for paradigms where query languages differ** across implementations (vector, search, graph). No abstractions where the language stays the same.
- **Pattern fluency over tech-coupling** — "a transport is a transport, a data store is a data store."
- **Every architectural choice should have an exit ramp** — ~1 week of localized work to revert.
- **Cadence during execution**: incremental commits per phase; verify each gate before moving on.
- **Skipped maderajuicer concessions:** no `int CurrentState`, no resx-based hand-rolled SQL, no two-table entity-plus-saga split.
