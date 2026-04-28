# Design Rationale and Decision Log

**Purpose for future sessions:** This document is the "why" companion to `architecture-plan.md`. The plan tells future-Claude *what* to build; this tells future-Claude *why those decisions* and *what was deliberately rejected*. Read this first when resuming so you don't re-propose abandoned approaches or miss user-preference signals that aren't expressible in the plan.

The plan is the source of truth for implementation. This doc is the source of truth for "why is the plan that shape" and "what does the user think and prefer." Where they conflict, plan wins; flag the inconsistency to the user.

---

## 1. Working frame

**Interview-demo intent.** This project exists for the owner to demonstrate architectural sophistication on GitHub for hyperscale-company interviews. Pattern fluency is the deliverable; data volume is incidental. Bias toward pattern-purity and reasoned complexity over minimalism. Frames like "over-engineered for scale" and "standard industry approach at this size" are *off-target* — complexity is the content, not a cost to minimize.

User's framing, preserved verbatim:
> "this is precisely a hobby project *because* i want to be able to competently interview at those massive companies. the individual technologies may be different, but a transport is a transport, and a data store is a data store. if i can demonstrate real troubleshooting with an overly-ambitious, open-source project that they can view themselves on github, that gives me a +1 in their eyes, i'd hope."

**Working cadence.** Bullet-sized responses, single point of discussion per turn, explicit hand-off questions. Don't preempt the user's thinking with multiple pre-answered questions in one response. User has explicitly pushed back on wall-of-text format mid-session.

**Defensiveness probes.** User periodically tests whether Claude will hold a position by proposing weak ideas to see if pushback comes. When a proposed shape has multiple independent problems, name all of them clearly. Don't soften structural critique into "small adjustment" language. User values directness over diplomacy.

**Exit ramps.** Every architectural choice should be reversible in ~1 week of localized work if pain emerges. Naming the exit ramp when proposing a design is a feature.

**Maderajuicer is reference, not gospel.** The owner has a similar-shape codebase at `/home/insta/src/repos/madera/maderajuicer` (CQRS, MT sagas, write-side/read-side split). It's a useful reference for *evolution of his thinking* — choices like saga-as-entity, sproc-populated read tables, hand-rolled SQL in resx — but he explicitly noted those were concessions made under constraints (couldn't get Mongo working, etc.). Read it for "decisions made by experience" but treat each pattern as evaluable, not authoritative. Several maderajuicer concessions were explicitly skipped in this project (`int CurrentState` → string, hand-rolled resx SQL → EF migrations, no separate entity table).

---

## 2. Saga-as-entity-projection

The chosen pattern: sagas ARE the canonical write-side entity store, not workflow-state-tables sitting next to entity-tables.

**Defense (the argument that dissolves the orthodox "saga is for workflow, repository is for state" critique):**

> User: "every 'entity' i've actually used in production doesn't exist in a vacuum. there are complex rules that ALWAYS come into play about when a value gets incremented or a timestamp set. layers upon layers of validation, logging, traces, tenancy. other systems need to know if and when this value changes."

The "entity in a vacuum" model that the orthodox critique relies on is a fiction. Real entities carry workflow semantics: authorization, audit, cross-system notification, conditional invariants. Calling them "CRUD" under-describes what's happening. The Discord ingestion domain is event-accumulation (Discord emits message events, Ollama emits tagging facts, Qdrant emits index confirmations) — nothing gets "updated" in a meaningful sense; entities exist as the accumulation of observed facts. Sagas model that reality; repositories would hide it.

**Linguistic framing that matters:** Don't say "saga as CRUD" — that concedes the frame. Use "sagas as entity projections," "sagas as materialized views of event streams," "consumers touch external systems and emit facts," "sagas accumulate facts into domain state."

**Acknowledged residual softness in the defense:**
- The domain-framing argument is partly aesthetic. Someone could reasonably model this as CRUD-with-event-outbox.
- Framework overhead is real regardless of framing (per-row serialization, version locking, MT wire-protocol coupling).

**Sharp answers to expected interview questions:**

Q: Why not event sourcing with repository pattern?
A: You lose explicit state machines. Sagas keep transitions as first-class inspectable artifacts — critical when debugging "why is this entity stuck."

Q: How does this scale?
A: At 5M instances, Mongo saga repos handle it fine with right knobs (InsertOnInitial, indexing, concurrency tuning). At 100× that, purpose-built event store (EventStoreDB / Kurrent). Same ballpark ceiling as other event-sourced patterns.

Q: Isn't this overengineering?
A: Over-engineered for the data volume; right-sized for the domain shape. Domain shape is event-accumulation, not state-mutation.

Q: What at the next scale?
A: Purpose-built event store for log; sagas as pure state machines reading from it. Saga-backing-directly-to-document-store is the intermediate-scale pattern.

**When this pattern doesn't apply:** CRUD-shaped domains (user profiles, product catalog), entity reads dominating writes 100×, MT being pulled in *just* for entity management, team uncomfortable with event-driven thinking, scale into dedicated event-store territory. None of those describe this project.

---

## 3. Event hierarchy and naming conventions

**Per-domain `*ModelBase` interfaces.** No global root (no universal `IModel`). Naming convention from maderajuicer evolution: **no `I` prefix** because these are MT message contracts; the interface-ness is incidental. Add `// ReSharper disable InconsistentNaming` per file.

```csharp
public interface MessageModelBase : CorrelatedBy<Guid>
{
    Guid MessageId { get; }
    long ChannelId { get; }
    /* ... entity state ... */
    new Guid CorrelationId => DeterministicGuid.FromSnowflake(MessageId);
}
```

**Events extend the model, depth ≤3 levels.** Versioning-free because new fields on Model propagate to every event; consumers don't need to worry about which version they got.

```
MessageModelBase
  └─ BaseMessageEvent                        [ExcludeFromTopology, ExcludeFromImplementedTypes]
       ├─ MessageCaptured
       ├─ MessageStateChanged                [ExcludeFromTopology, ExcludeFromImplementedTypes]
       │    ├─ MessageProjected
       │    ├─ MessageTagged
       │    └─ ...
       └─ MessageEditObserved
```

**Subscription rollup points.** A consumer of `MessageStateChanged` receives Projected, Tagged, Embedded etc. without explicit fan-in. A consumer of `BaseMessageEvent` receives everything in the hierarchy.

**`[ExcludeFromTopology, ExcludeFromImplementedTypes]`** on intermediate base interfaces — prevents MT from creating queues for the abstract layers. Discipline learned from maderajuicer.

**Subset interfaces (interface segregation) for read-side projection.** When a consumer needs only part of an entity's contract:
```csharp
public interface ChannelProjectionContextBase : CorrelatedBy<Guid> { /* 4 fields */ }
public interface ChannelModelBase : ChannelProjectionContextBase { /* full contract */ }
```

A consumer subscribing at `IConsumer<ChannelProjectionContextBase>` only sees fields it cares about; saga state implements the full superset.

**State is `string CurrentState`, not `int`.** maderajuicer uses `int` to save bytes; we don't have storage pressure. String state is ordinal-free.

**`ISystemClock` injected into state machines** for testability. `DeterministicGuid.FromSnowflake(messageId)` for correlation IDs (avoids EF client-side-eval warnings on SelectId paths).

---

## 4. The Message IR — the insight that simplified the saga

**The original frame (rejected):** Projection produces final-rendered display text. Saga carries that rendered text plus denormalized channel/guild context so it can re-render on changes. Maintenance via bulk-update consumer with `$inc(Version)` for concurrency control.

**Why it was rejected:** When asked "are these gaps unintentional artifacts of shoehorning the old workflow in," the answer turned out to be yes. The polling architecture stored final-rendered text in `messages` table because there was no separate read side. Carrying that assumption into CQRS forced denormalization-into-saga, which created a cascade of gaps:
- Stale projected content after channel renames.
- Ordering hazards: `MessageCaptured` carrying old context vs concurrent `ChannelChanged` carrying new.
- Schema migration via synthetic event publish for new fields.
- Fault-storms during heavy contention.

Splitting structural extraction (write-side, deterministic) from display rendering (read-side, current-state-aware) eliminates all four.

**The IR is a typed AST of the message** — references are typed IDs with `Fallback` snapshot strings. No rendering at projection time. Renderer walks IR + current entity state at read time.

```csharp
public abstract record MessageNode;
public sealed record TextNode(string Text) : MessageNode;
public sealed record MentionNode(MentionKind Kind, long? Id, string Fallback) : MessageNode;
public sealed record ChannelRefNode(long ChannelId, string Fallback) : MessageNode;
public sealed record EmojiNode(EmojiKind Kind, long? Id, string NameOrGlyph, bool Animated) : MessageNode;
public sealed record FormattingNode(FormattingKind Kind, IReadOnlyList<MessageNode> Children) : MessageNode;
/* ... CodeBlockNode, QuoteNode, LinkNode, TimestampNode ... */

public sealed record MessageIR(
    IReadOnlyList<MessageNode> Body,
    IReadOnlyList<AttachmentIR> Attachments,
    IReadOnlyList<EmbedIR> Embeds,
    ReplyContext? ReplyTo,
    DateTimeOffset CapturedAt);
```

**Tree (not flat list)** — Discord markdown supports nesting (`*bold _italic_*`).

**Storage:** typed BSON sub-document on saga side; `jsonb` column on read side. Not text-encoded JSON in either case — both stacks have native binary tree representations.

**Renderer:** `MessageRenderer.Render(MessageIR ir, RenderContext ctx)`. RenderContext is hydrated once per query (channel/role/user/emoji name dictionaries), not per message. Falls back to captured `Fallback` strings for unresolvable references (deleted users/channels).

**Why this earns its keep:**
- Channel/user/role renames apply retroactively at no write cost.
- Multiple render targets (web UI, plain-text MCP responses, embedding-input, RSS) consume the same IR.
- Search/indexing works on extracted plain text + reference IDs without re-parsing markdown.
- Saga doesn't need to subscribe to ChannelChanged/GuildChanged at all.

**Cost:** ~1.5–2× original payload size; one-time parsing cost at projection; renderer code on read side. Acceptable across the board.

---

## 5. Storage paradigm choices and abstractions

**Overall stack** (settled after lateral-thinking exercise):
- MongoDB for sagas.
- Postgres + extensions for read side: TimescaleDB (hypertables, continuous aggregates, compression), pgvector (vector search), native TSV (full-text), jsonb (IR), Apache AGE deferred.
- RabbitMQ single-node for transport (chosen for management UI).
- Ollama for local LLM.

**Why "Postgres + extensions" beat polyglot at this scale:**
- One operational target. One backup story. One deployment. EF Core works with all of it.
- Three-to-four query paradigms via extensions in a single instance.
- pgvector replaces standalone Qdrant at this scale; performance is competitive without the network hop. Standalone Qdrant is the stage-2 swap, behind `IVectorStore`.
- TimescaleDB is a strict superset of vanilla Postgres; "Postgres" in our docs means "Postgres + TimescaleDB" unless explicitly noted.

**Three storage abstractions** earn their keep at this stage; two don't.

**Earn abstraction (different query languages across implementations):**
- `IVectorStore` — pgvector today, dedicated Qdrant at scale. K-NN with metadata filter is the seam.
- `ISearchService` — Postgres TSV today, Tantivy/Meilisearch/Elasticsearch at scale. Query DSL changes between engines.
- `IConversationGraph` — recursive CTE today, Apache AGE later, Neo4j at hyperscale. SQL ↔ Cypher ↔ Gremlin all differ.

**Don't earn abstraction (query language stays the same until migration):**
- Analytical queries — SQL today, ClickHouse later. The SQL stays SQL until the migration; abstracting now is shape-without-purpose.
- Time-series operations — TimescaleDB IS Postgres. Hypertables look like tables. EF mappings are identical. Don't abstract over a Postgres extension.

**Migration corridors for future-me to articulate:**

| Capability | Today | Stage 2 (~500M) | Stage 3 (~5B+) |
|---|---|---|---|
| Vector | pgvector | dedicated Qdrant | Qdrant cluster |
| FTS | Postgres TSV | Tantivy/Meilisearch | Elasticsearch |
| Graph | recursive CTE | Apache AGE | Neo4j |
| Analytical | Postgres + matviews | + ClickHouse alongside | ClickHouse cluster |
| Time-series | TimescaleDB | TimescaleDB | TimescaleDB or split |

Each corridor is a single-implementation change behind an interface (or DDL change). The CQRS event-driven architecture is what makes those swaps localized.

---

## 6. Read-model consumer abstraction

The base class we landed on:

```csharp
public abstract class ReadModelBatchUpsertConsumer<TEvent, TEntity> : IConsumer<Batch<TEvent>>
    where TEvent : class where TEntity : class
{
    protected abstract TEntity Transform(TEvent message);

    public async Task Consume(ConsumeContext<Batch<TEvent>> context)
    {
        await using var db = await _factory.CreateDbContextAsync(context.CancellationToken);
        var entities = context.Message.Select(m => Transform(m.Message)).ToList();
        await db.BulkInsertOrUpdateAsync(entities, cancellationToken: context.CancellationToken);
    }
}
```

Per-entity Mapperly partial class generates the typed transforms:
```csharp
[Mapper] public static partial class MessageReadModelMapper
{
    public static partial ReadMessage Transform(MessageProjected evt);
    public static partial ReadMessage Transform(MessageEnriched evt);
}
```

Subclass body is ~5 lines: override `Transform` to call the static Mapperly method. Dependencies: `EFCore.BulkExtensions` + `Riok.Mapperly`. Both mature.

**Companion `ConsumerDefinition`** per consumer for `BatchOptions` (MessageLimit, TimeLimit, TimeLimitStart) and concurrency tuning. Auto-discovered via `bus.AddConsumers(assembly)`. Pattern from maderajuicer.

**Rejected shapes worth remembering:**
- `[Mapper] public static async ValueTask<DbReadModel> Transform<Saga, DbReadModel>(Saga instance)` — Mapperly can't source-generate from unbound generics; "Saga" is wrong parameter naming for an event input; generic call sites are more verbose. Surfaced by user as a defensiveness probe; final shape is per-entity typed overloads.
- Monolithic `IConsumer<UserCreated> + IConsumer<UserUpdated> + IConsumer<UserDeleted>` consumers — endpoint-level config (`ConcurrentMessageLimit`, `PrefetchCount`, retry policies) applies uniformly to all message types. `Batch<T>` forces per-type split anyway. The user's historical pattern; doesn't survive optimization.

---

## 7. Projection context — final shape

**Evolution across the conversation:**
1. First proposal: `ProjectionContextCache` carrying full ChannelContext/GuildContext objects. Rejected when projection moved from rendered-text to IR — full context wasn't needed.
2. Second proposal: `ProjectionNamespaceCache` carrying just channel-id-to-name and role-id-to-name dictionaries. Rejected after owner spotted that the cache is required pipeline infrastructure with warmup + drift surfaces, solving a sub-millisecond problem.
3. **Final: direct repository lookups in `ProjectMessageConsumer`** via `Task.WhenAll` — `(channelNames, roleNames) = await Task.WhenAll(_channelRepo.GetNamesAsync(...), _guildRepo.GetRoleNamesAsync(...))`. Sub-millisecond indexed point-fetches against Mongo. Stateless consumer; no warmup; no drift; no separate cache hosted-service.

**Cache as decorator deferred.** If profiling later shows the lookup is hot, wrap the channel/guild repos in caching decorators that subscribe to ChannelChanged/GuildChanged. Consumer signature unchanged; cache becomes implementation detail. Decorator pattern preserved for stage 2; v1 ships without.

**Sources of fallback strings:**
- User mentions: from Discord payload's `mentions[]` array (id + username, populated by Discord on send).
- Channel/role mentions: from direct repo lookup (the only thing the projection consumer actually fetches).
- Custom emoji: from the message's reference syntax itself (`<:name:id>`).

**Stamping the home channel onto MessageCaptured.** ChannelSyncConsumer already has the channel loaded during pagination; it can stamp the home-channel name onto MessageCaptured for any reference to *that* channel. Cross-channel `<#otherChannel>` refs still need the lookup. Common-case optimization that doesn't introduce a cache.

**Educational sidebar (not in our final design but worth carrying as pattern knowledge):**

There was a long discussion about how the original "saga carries denormalized ChannelContext" design would handle bulk refresh on `ChannelChanged`. Two relevant findings:

1. **MT's `CorrelateBy` query selectors do dispatch one event to N matching sagas, but iteration is strictly serial.** `SendQuerySagaPipe.cs` does a plain `foreach (var id in context) await SendToInstance(id)` with no parallelism knob. Mongo path: 1 `Find` + N serial `FindOneAndReplace`. EF path: 1 `SELECT FOR UPDATE` + N serial `SaveChangesAsync` inside one transaction (worse — long-held row locks + all-or-nothing rollback).

2. **The doc-DB-idiomatic alternative** is a dedicated `IConsumer<ChannelChanged>` that does `updateMany` against the saga collection directly, bypassing the state machine for the bulk path. Lifecycle stays in the state machine; denormalization maintenance is a separate write seam. The race with `FindOneAndReplace` (state machine clobbers concurrent `updateMany` updates) is solved by `$inc(Version)` on the bulk update — Mongo's atomic `$inc` plus MT's existing optimistic-concurrency check causes the state machine save to fault-and-retry, picking up fresh fields on retry. Equivalent to SQL's `SET Version = Version + 1` pattern.

These are worth knowing because they'll come up in interviews if anyone asks about denormalization patterns in document-DB-backed CQRS. Useful pattern knowledge, not part of our final architecture.

## 7.5. Saga orchestration: request/response over pub/sub

**Decision:** Inside the saga state machine, consumer calls use MT's `Request<TRequest, TResponse>` pattern. Pub/sub events are reserved for cross-saga and read-model fan-out.

**Why request/response inside the saga:**
- Saga is an explicit orchestrator, not an event source.
- Each step has a clear request/response contract: saga issues request, parks in `*.Pending`, resumes on response.
- MT auto-creates `*.Pending` states from Request declarations — no manual `Projecting` / `Tagging` state definitions.
- Faulted/timeout responses have first-class handling.
- Sagas remain "very short-lived per cycle" (each request is briefly pending) even though saga *instances* persist indefinitely.

**Why pub/sub for read-model fan-out:**
- Read-side `Batch<MessageStateChanged>` consumers don't reply; they just project.
- Multiple subscribers (read tables, vector index, search index) consume the same event stream.
- One-to-N fan-out is what pub/sub is for.

**Composite request consumers eliminate fan-in OCC collisions.** When a saga issues two parallel requests (e.g., Tag + Embed) and both responses land near-simultaneously, both response handlers race to update the saga doc. Optimistic concurrency forces one to fault and retry, but the retry loops are unpleasant under contention. Cleaner: collapse to one request that internally does both (`MessageEnhancementConsumer` doing Tag + Embed via `Task.WhenAll`, returning a combined response).

**`HasPendingEdit` flag for edits during in-flight requests.** `MessageEditObserved` arriving while saga is in `*.Pending` would be dropped if there's no handler. Pattern: `During([all *.Pending states], When(MessageEditObserved).Then(ctx => ctx.Saga.HasPendingEdit = true))`. On each `*.Completed` handler, check the flag and re-loop into `Request(ProjectMessage, ...)` if set. Preserves edits without race-condition magic.

**Multi-state `During` is the idiom for "any-of-N states + event".** No separate `Reprojecting` state needed; reuse `ProjectMessage.Pending` for both initial-projection and edit-triggered re-projection.

---

## 8. Maderajuicer evolution — what it tells us about user decision-making

Maderajuicer was analyzed to align with the owner's actual production patterns. The findings were initially read as "patterns to mirror" but the owner corrected the framing: **read maderajuicer as the trajectory of decisions made under specific constraints, not as a template.**

Specific concessions in maderajuicer that **we explicitly skip**:

- **Dapper + SQL Server saga repository.** Was used because Mongo wasn't available at the time. We use Mongo + MT Mongo saga repo.
- **`int CurrentState`.** Saves a few bytes at the cost of making integer values part of the storage contract. We use string state.
- **Hand-rolled SQL in resx files.** No compile-time validation, surgery across multiple file types for renames. We use EF migrations and Mapperly.
- **No separate entity table from saga table.** The owner merged them; saga IS entity. We keep that — it's a real architectural choice that survives, not a concession.
- **No EFCore.BulkExtensions or Mapperly in maderajuicer.** Absent; sproc-driven SELECT-INTO-temp-DELETE-INSERT instead. We DO use both because they fit our shape better.
- **Saga reload joins against `reports.Publishers` (the read-side mirror).** Couples saga hydration to read-consumer freshness. We avoid this — saga doesn't denormalize; ProjectionNamespaceCache is the only cross-entity read at write time, and it's a name dictionary, not a join.

**Patterns from maderajuicer we DID adopt:**
- `*ModelBase` interfaces, no `I` prefix.
- `[ExcludeFromTopology, ExcludeFromImplementedTypes]` on intermediate base interfaces.
- Saga-as-entity-projection (this is the ARCHITECTURAL pattern that survived; not a concession).
- `ConsumerDefinition` companion class per consumer.
- `ISystemClock` injection.
- CQRS write-side / read-side schema split (`data.*` vs `reports.*`).

**Decision-making takeaways for future-me when proposing tools or patterns:**
- When proposing a library, ask "would the owner have shed this in his evolution?" Mapperly/BulkExtensions: probably not — both were absent in maderajuicer because that codebase predated his interest in them, not because he rejected them. Owner explicitly confirmed he likes them in our stack.
- When proposing a separation, default to "merged" not "split" — owner merges entity-with-saga; he'd merge other things Claude might reflexively split.
- When proposing discipline cost, it has to clear "this prevents pain I've watched the owner solve" not "this is cleaner."
- The owner is willing to upgrade *away* from past concessions when the constraint that forced them is gone. Past doesn't bind future.

---

## 9. Meta-patterns about working style (preserved across sessions)

**Bullet-cadence over wall-of-text.** User pushed back hard on multi-paragraph responses; cadence is one discussion point per turn with explicit hand-off questions. Wall-of-text fills user's context window without giving them a chance to redirect.

**Defensiveness probes.** User periodically proposes weak ideas to test whether Claude will push back. Two probes happened in the original design conversation:
1. "Is this architecture too ambitious for me to actually build?" Claude held the critique but absorbed it via the interview-demo framing. Probe landed.
2. `Transform<Saga, DbReadModel>(Saga instance)` generic-static signature, explicitly described after as "a trick to see if you'd push back against an aggressively dumb idea." Claude noted the Mapperly-generics issue but called the overall shape "good" — drifted into soft validation. User surfaced it.

**Takeaway: when a proposed shape has multiple independent problems, name all of them. Don't soften into "small adjustment" language. User values directness over diplomacy; partial pushback reads as drift.**

**Exit-ramp discipline.** Every architectural choice should be reversible in ~1 week. Naming the exit ramp when proposing a design strengthens, not weakens, the proposal.

**Pattern fluency over tech lock-in.** "A transport is a transport, a data store is a data store." Don't argue for specific libraries when the pattern is what matters. If a library is wrong for a reason, say so — but don't conflate the library with the pattern.

**Maderajuicer treatment.** Reference it for *evolution of thinking*, not as a style guide to mirror. Owner explicitly de-authoritied it: "it had several concessions by that point."

**Lateral-thinking prompts.** When the user asks "is there a better X entirely" they mean it — don't anchor on the current proposal. Treat the prompt as license to evaluate paradigms genuinely. The IR-as-AST insight, the Postgres-with-extensions stack, and the cache-replaced-by-direct-lookup all came from lateral-thinking prompts.

**Spot-checks.** Owner reads the docs critically and challenges things that look like architectural drag. The `ProjectionNamespaceCache` survived two prior rounds before owner spotted "wait, this is required pipeline infrastructure for sub-millisecond lookups." When proposing additions to the architecture, ask "is this required pipeline infrastructure, and is it solving a real problem?" before writing it in. Owner will catch it if you don't.

---

## 10. Resumption guidance

After context compaction, re-read this document + the architecture plan in that order. Then:

1. Confirm with the user whether they're resuming execution or wanting more design discussion before code.
2. If executing: begin Phase 1 per `architecture-plan.md`'s phased plan. All major decisions are settled (transport, saga store, storage stack, saga lifecycle, orchestration model, projection pattern, schema policy).
3. If discussing: push back gently if the user re-opens settled territory; ask whether they want to revisit or just understand.

Working branch: `initial`. Last commit: `03c13b1` (pin work snapshot). Don't rebase or reset; build forward.

**Anchor for sanity-checking proposals against the settled architecture:**
- Sagas use **request/response** for orchestration. Pub/sub is for read-model fan-out only.
- Sagas **persist indefinitely**. No completion-and-delete.
- ChannelSaga has **no terminal state**. Cursor-based progress; channel-exists = monitor forever.
- Saga state holds **lifecycle facts only**. No cross-entity denormalization.
- Projection emits **IR**, not rendered text. Display rendering is read-side.
- ProjectMessageConsumer does **direct repo lookups** for fallback strings. No cache (decorator deferred).
- **MessageEnhancementConsumer** is composite (Tag + Embed internally parallel). Avoids saga-fan-in.
- `HasPendingEdit` flag handles edits arriving during in-flight requests.
- Multi-state `During([stateA, stateB], When(event))` is the re-projection idiom.
- v1 has **no migrations**. Drop and recreate as needed. Models on disk are v1.
- **Text channels only** for v1. Forum/voice/etc. deferred.

The rewrite is substantial but bounded. Most domain code survives (Discord client, Ollama clients, projection logic, substantiveness filter). What changes is orchestration shape and where state lives.
