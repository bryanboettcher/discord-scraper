# Restart prompt — #11 closed; Tag/Classify event-driven; TimeProvider migrated

Paste below as the opening message of a fresh Claude session.

---

Continuing the discord-scraper project at `/home/insta/src/bryanboettcher/discord-scraper`.

**Memory at `/home/insta/.claude/projects/-home-insta-src-bryanboettcher-discord-scraper/memory/` is extensive — load it before responding.** New entries this session:

- `feedback_e11000_collection_disambiguation.md` — E11000 log lines are ambiguous in MT+Mongo+outbox; identify the collection (saga vs outbox `InboxState`) before diagnosing
- `feedback_mt_middleware_first.md` — reach for MT's first-class middleware (CircuitBreaker/KillSwitch/RateLimit/ConcurrentMessageLimit) before designing app-level coordination
- `feedback_mt_fault_auto_publish.md` — unhandled consumer exceptions auto-publish `Fault<TMessage>`; the saga subscribes directly via `Event<Fault<TRequest>>` + `CorrelateById`; no custom failure-event contracts needed
- `feedback_dispatch_over_incremental_patching.md` — when validation surfaces a recurring failure pattern across N sites, re-dispatch the engineer; don't patch one-at-a-time inline

## Current state

- Branch: `main`. HEAD `fa6f859`, pushed to origin (`b3a4753..fa6f859`, 10 session commits).
- Working tree clean except `.planning/restart-prompt.md` (this file).
- Wiki at `/home/insta/src/bryanboettcher/discord-scraper.wiki`: Home + ADR-001/002/003/004. Not updated this session.
- Docker stack: **still running** with the validation data — 306 message sagas in terminal states (291 Enriched + 15 Excluded), 17 channel sagas in CaughtUp, 1 guild saga in Synced. Per `feedback_docker_lifecycle.md`, tear down before session end if no longer needed: `docker compose down -v`.

## Issues filed

| # | Title | State |
|---|---|---|
| 1 | Channel backlog routing iteration | open |
| 2 | Admin saga inspection migration | open |
| 3 | EfCoreBulkWriter metadata caching | open |
| 4 | FakeBatch dedupe | open |
| 5 | Live-scrape Tag/Classify verification | open (functionally satisfied this session — could close) |
| 6 | Test suite performance | open |
| 7 | Test fixture infrastructure umbrella | closed |
| 8 | EfCoreBulkWriter Postgres 42P10 | closed |
| 9 | Saga RequestTimeoutExpired chokepoint (Bug B) | open (was downstream of #11; remaining concern is real LLM-paced backpressure — covered by existing KillSwitch on TagConsumer/ClassifyConsumer) |
| 10 | ReadMessageBulkWriter | open (placeholder) |
| 11 | MessageSaga: RequestTimeoutExpired E11000 | **closed by `0155098` + validated empirically** |

## What landed this session

Starting from `b3a4753` plus two pre-session unpushed commits (saga refactor `751443d`, GuildSaga `SyncedAt` `3657273`).

1. **Test infrastructure for #11 — `f0ee565` + `c724cb7`**: TestSupport now has `WithMongoOutbox()` (replica-set Mongo + bus-level `AddMongoDbOutbox`), `WithCapturingLogger()` (log sink with `.DuplicateKeyErrors` filter), `WithNeverRespondingAnalyze()` (stub that ACKs Analyze requests but never responds). New `Issue11ReproTests.cs` E2E regression fixture. `f0ee565` is the one-line `SyncedAt` fix on `TestGuildChanged` fixture (build break from `3657273`).

2. **#11 root cause + fix — `0155098`**: All three saga state machines included `Initial` in their `During(allStates, ...)` catch-all. MT's `MessageEventCorrelation.cs:61` checks `_includesInitial` to decide saga policy — if any event registration is reachable from `Initial`, the policy becomes `NewOrExistingSagaPolicy` which calls `MissingSagaPipe.Save → bare InsertOneAsync` on missing instance instead of discarding. When `RequestTimeoutExpired<T>` arrived after MT cleared the saga's RequestId field on Completed, the query found nothing and tried to insert → E11000 on the existing `_id`. Fix: trim `Initial` from `allStates` (hoist `UpdateSaga` into `Initially(...)` to preserve CreatedOn stamping), add `OnMissingInstance.Discard` defensively on all four `Request(...)` declarations in MessageSagaStateMachine.

3. **Live validation of #11 fix**: After rebuild, the same RMQ scheduled-timeout messages from the buggy run fired against the new code → all silently discarded. Zero E11000 in 60 seconds vs ~3500 in the prior 60-second window.

4. **Event-driven Tag/Classify — `eecae7f`**: Converted Tag and Classify (slow LLM stages) from `Request<>/Response<>` to `Publish/Subscribe` with `Fault<T>` handling. Analyze and Project stay as `Request<>` (fast, sub-second). New contracts `TagMessageRequested` and `ClassifyMessageRequested`; `MessageClassified` gained `IndexedAt` (required by `ClassificationInvalidated` predicate). Saga state machine adds `Event<MessageTagged>`, `Event<MessageClassified>`, `Event<Fault<TagMessageRequested>>`, `Event<Fault<ClassifyMessageRequested>>` correlated by snowflake-Guid. Consumers throw normally; MT auto-publishes `Fault<T>` on exception (no try/catch). Saga loses `TagRequestId`/`ClassifyRequestId` fields.

5. **BSON schema-evolution defense — `5481e46`**: Added `cm.SetIgnoreExtraElements(true)` to all three saga BSON class maps. Caught during validation: dropping `TagRequestId`/`ClassifyRequestId` from `MessageSagaState` caused `FormatException: Element 'TagRequestId' does not match any field or property` when MT deserialized pre-event-conversion docs. With this on, removed fields just get ignored on read.

6. **Live replay validation**: 198 Faulted sagas from the prior buggy run replayed cleanly via `POST /api/admin/sagas/messages/replay-faulted`. 150 reached Enriched immediately; the remaining 48 progressed through `Classifying` sequentially (ConcurrentMessageLimit=1 GPU lock on Classify is the intentional cost-discipline mechanism — single LLM call at a time across the whole ingester). Final state: 291 Enriched + 15 Excluded + 0 Faulted.

7. **TimeProvider migration — `8bf97f6` + `fa6f859`**: Replaced custom `ISystemClock`/`SystemClock` (only exposed `UtcNow`) with .NET 8+ built-in `TimeProvider`. Production uses `TimeProvider.System`. Tests use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` (10.6.0). `LatencyProfile<TIn>` and all subtypes (Constant, Range, Lognormal, Drift, FromInput) accept an optional `TimeProvider`; defaults to `System.TimeProvider.System`. All wall-clock-asserting tests in `LatencyProfileTests` + `StubTaggingClientTests` + `StubEmbeddingClientTests` rewritten to use `FakeTimeProvider.Advance(...)` for deterministic assertions. The Dockerfile flaky-test filter (`b53daaa`) was reverted as part of the engineer's commit — no longer needed.

## Material findings worth remembering

**The `_includesInitial` footgun.** Any `Event(...)` registered for a `During(allStates, ...)` block where `allStates` includes `Initial` makes MT think the event is initial-reachable. That flips the saga policy from `AnyExistingSagaPolicy` (silent discard on missing) to `NewOrExistingSagaPolicy` (insert on missing). For auto-generated `RequestTimeoutExpired<T>` events whose RequestId-field correlation can legitimately miss (the field was cleared on Completed), this means InsertOnInitial-equivalent behavior — bare `InsertOneAsync` against an existing `_id` → E11000. The fix is `Initial` not in the catch-all. Codified in `feedback_e11000_collection_disambiguation.md` indirectly; the specific mechanism is in #11's close-out comment.

**MT's `Fault<TMessage>` auto-publish is the right failure path.** Don't propose `MessageTagFailed`/custom failure events for consumer exception handling. The consumer throws; MT publishes `Fault<TMessage>`; the saga subscribes via `Event<Fault<TRequest>>` correlated by `ctx.Message.Message.MessageSnowflake` (outer = Fault envelope, inner = original payload). Codified in `feedback_mt_fault_auto_publish.md`.

**E11000 log lines are collection-ambiguous under MT+outbox.** MT's `MongoDbOutbox` uses `_id` unique constraints on its `InboxState` collection and legitimately throws E11000 on concurrent redelivery dedup — that's not a bug. When grepping logs for E11000, capture the full `MongoBulkWriteException.WriteErrors[].Message` which contains the collection name. The session's `CapturingLoggerProvider.DuplicateKeyErrors` filter was string-grep only at first; it caught real bugs but would also catch outbox false positives. Codified in `feedback_e11000_collection_disambiguation.md`.

**Saga BSON schema evolution requires `SetIgnoreExtraElements(true)`.** Otherwise removing a field from saga state breaks deserialization of any pre-existing document. Applied to all three saga class maps in `MongoBsonRegistration.cs`.

**MT consumer endpoint middleware composition for slow stages.** Already in place at `TagConsumerDefinition` (KillSwitch outer, MessageRetry middle with `Handle<HttpRequestException>+TaskCanceledException` and `Ignore<InvalidOperationException>`, ConcurrentMessageLimit innermost) and `ClassifyConsumerDefinition` (KillSwitch + `ConcurrentMessageLimit=1` + deliberately NO retry — "let the saga see the fault and replay later when the admin endpoint or a scheduled fan-out fires"). Codified in `feedback_mt_middleware_first.md`.

**Replay path uses `MessageReplayRequested` fan-out**: `POST /api/admin/sagas/messages/replay-faulted[?phase=tag|classify]` publishes a single `MessageReplayRequested` event; the saga state machine's `During(Faulted, When(MessageReplayRequested, predicate))` handlers fan-out to all matching Faulted sagas via `CorrelateBy` (phase-keyed by `Embedding == null` for Tag, `Embedding != null && Tags == null` for Classify). Verified working on 198 Faulted sagas this session.

## Active state to triage

- **Working tree**: clean except this restart prompt.
- **Stack**: still running with 306 sagas in terminal states. Tear down with `docker compose down -v` if not continuing.
- **Pre-existing flaky tests** that are NOT yet converted to FakeTimeProvider: search `tests/DiscordScraper.E2E.Tests/Consumers/ConsumerRoutingTests.cs` for `Stopwatch` — those E2E tests assert wall-clock dwell time. They're in the E2E slnx, not the main one, so they don't break docker builds. Defer until the relevant tests prove flaky in practice.
- **Issue #5** (live-scrape Tag/Classify verification): functionally satisfied this session against the small test guild (`1374441548594282659`). Could close with a comment, or keep open for "larger guild stress test TBD" — `project_test_guild_ids.md` notes the larger guild is TBD.
- **Issue #9** (saga chokepoint): the original "RequestTimeoutExpired chokepoint" hypothesis was downstream of #11. After the fix + event-driven conversion, the only "chokepoint" is the legitimate GPU lock on Classify (ConcurrentMessageLimit=1). That's intentional cost discipline. Could close #9 with that conclusion, or keep open as a tracking issue for future KillSwitch tuning under real million-message-scale load.

## Recommended next session sequence

1. **Tear down the validation stack** if not continuing live work: `docker compose down -v`.
2. **Optionally close #5 and #9** with comments documenting this session's empirical findings.
3. **Pick from remaining open issues** by priority. #1 (channel backlog routing) is the next-largest architectural item; #2 (admin saga inspection) is a useful operational tool. #3/#4/#6 are polish.
4. **Or do something new entirely** — the saga subsystem is now in a known-good state at million-message-scale-ready architecture.

## Tone reminder

Bryan's collaboration style: direct, anti-hedge, expects substantive pushback when designs have real flaws. Memory entries codify this. Don't manage his time. Don't bounce ops back to him to run by hand — delegate to subagents and let them execute. Push only when explicitly instructed; otherwise commit freely.
