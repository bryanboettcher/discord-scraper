# MessageSagaStateMachine refactor plan

Consolidated from design discussion in [src/DiscordScraper.Write/Sagas/MessageSagaStateMachine.cs](../src/DiscordScraper.Write/Sagas/MessageSagaStateMachine.cs).

## Reference patterns

- [SellableItemStateMachine.cs:302-319](../../kb-platform/src/services/KbStore.Storefront/Domains/SellableItems/SellableItemStateMachine.cs) — `{ get; }` autoprops on a state-machine class. MT initializes via reflection on the readonly backing field.
- [SellableItemStateMachine.cs:404-407 + caller :56](../../kb-platform/src/services/KbStore.Storefront/Domains/SellableItems/SellableItemStateMachine.cs) — `Then(UpdateTimestamp)` with `void UpdateTimestamp(BehaviorContext<TSaga>)` saga-only method group on a typed-event binder.

## Scope-defining decisions

| Decision | Outcome |
|---|---|
| `{ get; private set; } = null!` syntax | Drop `null!`, drop `private set`. Suppress CS8618 via `<NoWarn>` on the project. |
| `ILogger<T>` ctor injection | Drop. Use `LogContext.Current?.Error(...)`. |
| `ISystemClock` ctor injection | Drop. Use `ctx.SentTime` (publisher's stamp) for `UpdatedOn` / `SettledOn`. |
| `ctx.Init<T>(new {...})` | Replace with direct construction. Records are concrete, MT's interface-flexibility pretext doesn't apply. |
| Saga → message projection placement | Extension methods on `MessageSagaState` in a `MessageSagaStateExtensions` static class in `DiscordScraper.Write.Sagas`. Direction stays `Write → Contracts`. |
| `MessageClassified` + `MessageEnriched` terminal pair | Collapse — `MessageClassified` has zero subscribers, `MessageEnriched` is a strict superset. Drop `MessageClassified`. Keep mid-saga phase events (`MessageAnalyzed` / `MessageProjected` / `MessageTagged`) — distinct lifecycle points, future-consumer audience. |
| `IfElse` binder | Replace with `When(event, predicate)` + `When(event, !predicate)`. Predicates as inline lambdas reading saga properties. |
| `FaultedHandler` / `TimeoutHandler` helpers | Inline at call sites with method-group statics: `.Then(SettleSaga).Then(LogFault).TransitionTo(Faulted)`. |
| Helper method generics | Saga-only signatures wherever possible (`BehaviorContext<MessageSagaState>` for `UpdateSaga` / `SettleSaga`). Generic over `TRequest` only where the body reads `ctx.Message` (`LogFault`, `LogTimeout` for `Fault<TRequest>` / `RequestTimeoutExpired<TRequest>` payloads). |
| `CorrelateBy(... PhaseMatches(saga, ctx.Message.Phase))` at lines 131-137 | **Pre-existing latent bug.** Expression tree gets translated by MongoDB.Driver's LINQ provider; static-method invocation with switch expression is non-translatable. Fix by inlining the switch into the expression. Independent of refactor; ship as step 0. |

## Helper signatures

```csharp
// Saga-only — no generic. Method-group target for .Then(...)
private static void UpdateSaga(BehaviorContext<MessageSagaState> ctx)
    => ctx.Saga.UpdatedOn = ctx.SentTime ?? ctx.Saga.UpdatedOn;

private static void SettleSaga(BehaviorContext<MessageSagaState> ctx)
    => ctx.Saga.SettledOn = ctx.SentTime ?? ctx.Saga.UpdatedOn;

// Generic — needs the message payload
private static void LogFault<TRequest>(BehaviorContext<MessageSagaState, Fault<TRequest>> ctx)
    where TRequest : class
    => LogContext.Current?.Error(
        "{Step} faulted for Snowflake={Snowflake}: {Exceptions}",
        typeof(TRequest).Name,
        ctx.Saga.MessageSnowflake,
        string.Join("; ", ctx.Message.Exceptions.Select(e => e.Message)));

private static void LogTimeout<TRequest>(BehaviorContext<MessageSagaState, RequestTimeoutExpired<TRequest>> ctx)
    where TRequest : class
    => LogContext.Current?.Error(
        "{Step} timed out for Snowflake={Snowflake}",
        typeof(TRequest).Name,
        ctx.Saga.MessageSnowflake);
```

## CorrelateBy fix shape

```csharp
Event(() => MessageReplayRequested, e =>
{
    e.CorrelateBy((saga, ctx) =>
        saga.CurrentState == nameof(Faulted)
        && (ctx.Message.Phase == null
            || (ctx.Message.Phase == "tag" && saga.Embedding == null)
            || (ctx.Message.Phase == "classify" && saga.Embedding != null && saga.Tags == null)));
    e.OnMissingInstance(m => m.Discard());
});
```

Every operator is one MongoDB.Driver's LINQ provider can translate. `PhaseMatches` static is removed.

## Resulting call-site shape (Classifying as exemplar)

```csharp
During(Classifying,
    When(ClassifyRequest.Completed)
        .Then(CopyClassifyResult),                          // always runs first

    When(ClassifyRequest.Completed, ctx => ctx.Saga.HasPendingEdit)
        .Then(ResetEnrichmentForReproject)
        .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
        .TransitionTo(ProjectMessage.Pending),

    When(ClassifyRequest.Completed, ctx => !ctx.Saga.HasPendingEdit)
        .PublishAsync(ctx => ctx.Saga.ToMessageEnriched())
        .Then(SettleSaga)
        .TransitionTo(Enriched),

    When(ClassifyRequest.Faulted)
        .Then(SettleSaga)
        .Then(LogFault)
        .TransitionTo(Faulted),

    When(ClassifyRequest.TimeoutExpired)
        .Then(SettleSaga)
        .Then(LogTimeout)
        .TransitionTo(Faulted));
```

Note: registration order matters. The unconditional `When(ClassifyRequest.Completed)` runs first, then the predicate branches see the updated state. Mutex is enforced by `ctx.Saga.HasPendingEdit` vs `!ctx.Saga.HasPendingEdit` — only one branch fires per dispatch.

## Execution order

| Step | Change | Verification |
|---|---|---|
| 0 | Inline-expand `PhaseMatches` into `CorrelateBy` expression. Delete `PhaseMatches` static. | Existing replay-from-faulted tests pass. Independent of remaining refactor. |
| 1 | Drop `null!`, drop `private set` on event/state/request properties. Add `<NoWarn>CS8618</NoWarn>` to the Write `.csproj` (or scoped `#nullable disable` if narrower control wanted). | `dotnet build` clean. Saga registration tests pass. |
| 2 | Drop `ILogger<MessageSagaStateMachine>` ctor param. Switch helpers to `LogContext.Current?.Error(...)`. | Saga unit tests pass. |
| 3 | Add `MessageSagaStateExtensions` static class with `ToProjectRequest` / `ToTagRequest` / `ToClassifyRequest` / `ToAnalyzeRequest` / `ToMessageAnalyzed` / `ToMessageProjected` / `ToMessageTagged` / `ToMessageEnriched`. | Compiles. |
| 4 | Replace `ctx.Init<T>(new {...})` with `ctx.Saga.ToX()` calls everywhere. Drop terminal `Publish(MessageClassified)`. | Saga unit tests pass; verify `MessageEnriched` payload assertions. |
| 5 | Replace `IfElse(...)` with `When(event, predicate) + When(event, !predicate)` shape across all four phases. | Saga unit tests pass. Confirm registration-order semantics with a state-update-then-branch test. |
| 6 | Replace `FaultedHandler` / `TimeoutHandler` helper methods with inline call-site DSL. Add `LogFault<T>` / `LogTimeout<T>` generic statics. | Saga unit tests pass. |
| 7 | Drop `ISystemClock` ctor param. `UpdateSaga` / `SettleSaga` switch to `ctx.SentTime`. Remove clock from saga test fixtures. | Saga unit tests pass. Timestamp-related assertions adjusted. |
| 8 | Audit `ChannelSagaStateMachine` for the same patterns; apply matching changes. | Channel-saga unit tests pass. |

## Out of scope for this plan

- Five-test saga timeout investigation (#9) — runs after this refactor lands and #11 is understood.
- Channel saga "stuck in null state" diagnosis — separate audit; this refactor may surface or fix it incidentally, but it's not the goal.
