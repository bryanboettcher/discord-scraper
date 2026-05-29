// CS8618/CS9264: MT initializes Event/State/Request properties via reflection on backing fields.
// CS8602/CS8601: dereferencing those properties in the constructor (e.g. State[] allStates,
// catch-all During loops) is safe at runtime but unprovable by flow analysis.
#pragma warning disable CS8618, CS9264, CS8602, CS8601

using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using MassTransit;
using MassTransit.Contracts;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Orchestrates the message lifecycle:
/// Analyze → Project → Tagging → Classifying → Enriched.
///
/// Tag and Classify are sequential, saga-driven phases. Analyze and Project use
/// Request&lt;&gt;/Response&lt;&gt; (fast, CPU-bound). Tag (embedding) and Classify (LLM) use
/// event-driven Publish/Subscribe so the saga waits indefinitely; the consumer's
/// HttpClient timeout is the operational deadline. Consumer exceptions auto-publish
/// Fault&lt;T&gt; which the saga subscribes to for fault handling and replay.
///
/// Edits arriving while a request is in flight are queued via HasPendingEdit and re-loop
/// through ProjectMessage on the next *.Completed handler. Edits in the Enriched terminal
/// state trigger an immediate re-loop.
///
/// Replay: Faulted sagas receive MessageReplayRequested and re-enter Tagging or Classifying
/// based on which phase produced no results.
/// </summary>
public sealed class MessageSagaStateMachine : MassTransitStateMachine<MessageSagaState>
{
    // 2015-01-01T00:00:00Z in Unix ms. Snowflake upper 42 bits encode (unixMs - epoch).
    // Inlined to avoid taking a project reference on DiscordScraper.Discord.
    private const long DiscordEpochMs = 1420070400000L;

    private readonly TimeSpan _requestTimeout;

    public State Excluded { get; }
    public State Faulted { get; }

    /// <summary>Terminal happy-path state. Saga persists indefinitely for edit re-loops.</summary>
    public State Enriched { get; }

    public State Tagging { get; }
    public State Classifying { get; }

    public Event<MessageCaptured> MessageCaptured { get; }
    public Event<MessageEditObserved> MessageEditObserved { get; }
    public Event<MessageReplayRequested> MessageReplayRequested { get; }
    public Event<TagsInvalidated> TagsInvalidated { get; }
    public Event<ClassificationInvalidated> ClassificationInvalidated { get; }

    // Tag and Classify phases — event-driven; no Request<> timeout.
    public Event<MessageTagged> Tagged { get; }
    public Event<MessageClassified> Classified { get; }
    public Event<Fault<TagMessageRequested>> TagFault { get; }
    public Event<Fault<ClassifyMessageRequested>> ClassifyFault { get; }

    public Request<MessageSagaState, AnalyzeMessageRequest, AnalyzeMessageResponse> AnalyzeMessage { get; }
    public Request<MessageSagaState, ProjectMessageRequest, ProjectMessageResponse> ProjectMessage { get; }

    /// <param name="requestTimeout">Override all request timeouts. Tests only; production resolves IOptions from DI.</param>
    public MessageSagaStateMachine(TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);

        InstanceState(x => x.CurrentState);

        // ---------------------------------------------------------------------
        // Event registrations
        // ---------------------------------------------------------------------

        Event(() => MessageCaptured, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => BuildSagaFromCapture(ctx.Message));
        });

        Event(() => MessageEditObserved, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake)));

        // Fan-out to all Faulted sagas matching the requested phase filter.
        // Predicate is inlined (rather than calling a static helper) because CorrelateBy
        // takes Expression<Func<...>> — the saga repository translates it to the persistence
        // layer's query language. MongoDB.Driver's LINQ provider cannot translate a static
        // method invocation containing a switch expression; non-translatable nodes either
        // throw or fall back to a full-table client-side scan. Every operator below is one
        // the Mongo translator handles directly.
        //
        // "tag"      — saga has no embedding (tag/embedding phase faulted)
        // "classify" — embedding present but no tags (classify/LLM phase faulted)
        // null/other — matches every Faulted saga (preserves prior PhaseMatches default)
        Event(() => MessageReplayRequested, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Faulted)
                && (ctx.Message.Phase == null
                    || (ctx.Message.Phase == "tag" && saga.Embedding == null)
                    || (ctx.Message.Phase == "classify" && saga.Embedding != null && saga.Tags == null)
                    || (ctx.Message.Phase != "tag" && ctx.Message.Phase != "classify")));
            e.OnMissingInstance(m => m.Discard());
        });

        // Fan-out for re-tagging under a new embedding model. Re-runs both Tag (embedding)
        // and Classify so downstream vector points and LLM tags stay consistent.
        //
        // Cutoff is compared to saga.IndexedAt (stamped from MessageClassified.IndexedAt)
        // rather than saga.UpdatedOn. UpdatedOn is the envelope-time tracking field stamped from
        // ctx.SentTime — not under test control, since MT does not expose a public hook to set
        // SentTime on outbound messages. IndexedAt is a contract-carried business timestamp from
        // the classify event, so tests can drive deterministic cutoffs against it.
        Event(() => TagsInvalidated, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.EmbeddingModelVersion != ctx.Message.ModelVersion
                && saga.IndexedAt < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });

        // Fan-out for re-classification under a new LLM model. Embedding (Tag phase) is preserved;
        // only Classify is re-run.
        Event(() => ClassificationInvalidated, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.ClassifyModelVersion != ctx.Message.ModelVersion
                && saga.IndexedAt < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });

        // Tagged and Classified are published by their respective consumers. Correlation is by
        // DeterministicGuid.FromSnowflake so any node receiving a snowflake computes the same key.
        Event(() => Tagged, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake)));

        Event(() => Classified, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake)));

        // Fault<T> outer Message is the Fault<T> envelope; inner Message is the original payload.
        // OnMissingInstance.Discard: if the saga was already advanced or deleted before the fault
        // arrived (race / duplicate), silently discard rather than creating a phantom instance.
        Event(() => TagFault, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.Message.MessageSnowflake));
            e.OnMissingInstance(m => m.Discard());
        });

        Event(() => ClassifyFault, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.Message.MessageSnowflake));
            e.OnMissingInstance(m => m.Discard());
        });

        // ---------------------------------------------------------------------
        // Request registrations (Analyze + Project only)
        // ---------------------------------------------------------------------

        Request(() => AnalyzeMessage, x => x.AnalyzeMessageRequestId, r =>
        {
            r.Timeout = _requestTimeout;
            // TimeoutExpired events arrive after MT has already completed/cleared the saga's
            // RequestId — the ById correlation finds no instance. Without Discard the missing-
            // instance policy falls back to NewOrExistingSaga (because Initial is initial-reachable
            // via the catch-all), which tries InsertOneAsync and hits E11000 on the existing doc.
            r.TimeoutExpired = cfg => cfg.OnMissingInstance(m => m.Discard());
        });
        Request(() => ProjectMessage, x => x.ProjectMessageRequestId, r =>
        {
            r.Timeout = _requestTimeout;
            r.TimeoutExpired = cfg => cfg.OnMissingInstance(m => m.Discard());
        });

        // ---------------------------------------------------------------------
        // Initial: capture → analyze
        // ---------------------------------------------------------------------

        Initially(
            When(MessageCaptured)
                .Then(ctx => CopyCaptureFields(ctx.Saga, ctx.Message))
                .Then(UpdateSaga)
                .Request(AnalyzeMessage, ctx => ctx.Saga.ToAnalyzeRequest())
                .TransitionTo(AnalyzeMessage.Pending));

        // ---------------------------------------------------------------------
        // AnalyzeMessage.Pending: substantive non-bot → Project, else → Excluded
        // ---------------------------------------------------------------------

        During(AnalyzeMessage.Pending,
            // Unconditional: copy response fields and publish phase event before branching.
            When(AnalyzeMessage.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.IsSubstantive = ctx.Message.IsSubstantive;
                    ctx.Saga.IsBot = ctx.Message.IsBot;
                    ctx.Saga.DetectedLanguage = ctx.Message.DetectedLanguage;
                })
                .PublishAsync(ctx => ctx.ToMessageAnalyzed()),

            // Substantive non-bot: continue to projection.
            When(AnalyzeMessage.Completed, ctx => ctx.Saga.IsSubstantive == true && ctx.Saga.IsBot != true)
                .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
                .TransitionTo(ProjectMessage.Pending),

            // Non-substantive or bot: settle and park in Excluded.
            When(AnalyzeMessage.Completed, ctx => !(ctx.Saga.IsSubstantive == true && ctx.Saga.IsBot != true))
                .Then(SettleSaga)
                .TransitionTo(Excluded),

            When(AnalyzeMessage.Faulted)
                .Then(SettleSaga)
                .Then(LogFault)
                .TransitionTo(Faulted),

            When(AnalyzeMessage.TimeoutExpired)
                .Then(SettleSaga)
                .Then(LogTimeout)
                .TransitionTo(Faulted));

        // ---------------------------------------------------------------------
        // ProjectMessage.Pending: build IR; if edit pending → re-project, else → Tagging
        // ---------------------------------------------------------------------

        During(ProjectMessage.Pending,
            // Unconditional: capture IR from response before branching.
            When(ProjectMessage.Completed)
                .Then(ctx => ctx.Saga.IR = ctx.Message.IR),

            // Edit arrived during in-flight request — re-project so embedding uses latest content.
            When(ProjectMessage.Completed, ctx => ctx.Saga.HasPendingEdit)
                .Then(ctx => ctx.Saga.HasPendingEdit = false)
                .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
                .TransitionTo(ProjectMessage.Pending),

            // No pending edit: publish phase event, clear stale enrichment, advance to Tagging.
            When(ProjectMessage.Completed, ctx => !ctx.Saga.HasPendingEdit)
                .PublishAsync(ctx => ctx.ToMessageProjected())
                .Then(ctx =>
                {
                    // Clear Tag/Classify results before re-entering Tagging so a
                    // re-project always produces fresh enrichment.
                    ctx.Saga.Tags = null;
                    ctx.Saga.ClassifyModelVersion = null;
                    ctx.Saga.Embedding = null;
                    ctx.Saga.EmbeddingModelVersion = null;
                    ctx.Saga.IndexedAt = null;
                })
                .Publish(ctx => ctx.Saga.ToTagMessageRequested())
                .TransitionTo(Tagging),

            When(ProjectMessage.Faulted)
                .Then(SettleSaga)
                .Then(LogFault)
                .TransitionTo(Faulted),

            When(ProjectMessage.TimeoutExpired)
                .Then(SettleSaga)
                .Then(LogTimeout)
                .TransitionTo(Faulted));

        // ---------------------------------------------------------------------
        // Tagging: store embedding; if edit pending → re-project, else → Classify
        // ---------------------------------------------------------------------

        // Tag phase = embedding (nomic-embed-text). On success: embedding vector stored, then Classify fires.
        During(Tagging,
            // Unconditional: store embedding from event before branching.
            When(Tagged)
                .Then(ctx =>
                {
                    ctx.Saga.Embedding = ctx.Message.Embedding.ToArray();
                    ctx.Saga.EmbeddingModelVersion = ctx.Message.EmbeddingModelVersion;
                }),

            // Edit arrived — discard embedding and re-project from latest content.
            When(Tagged, ctx => ctx.Saga.HasPendingEdit)
                .Then(ctx =>
                {
                    ctx.Saga.HasPendingEdit = false;
                    ctx.Saga.Embedding = null;
                    ctx.Saga.EmbeddingModelVersion = null;
                })
                .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
                .TransitionTo(ProjectMessage.Pending),

            // No pending edit: advance to Classifying.
            When(Tagged, ctx => !ctx.Saga.HasPendingEdit)
                .Publish(ctx => ctx.Saga.ToClassifyMessageRequested())
                .TransitionTo(Classifying),

            // Tag failure → Faulted with no embedding stored.
            When(TagFault)
                .Then(SettleSaga)
                .Then(LogFault)
                .TransitionTo(Faulted));

        // ---------------------------------------------------------------------
        // Classifying: store tags + IndexedAt; if edit pending → re-project, else → Enriched
        // ---------------------------------------------------------------------

        // Classify phase = LLM topic tagging + vector store indexing. On success: tags + IndexedAt stored.
        During(Classifying,
            // Unconditional: store classify results from event before branching.
            When(Classified)
                .Then(ctx =>
                {
                    ctx.Saga.Tags = ctx.Message.Tags;
                    ctx.Saga.ClassifyModelVersion = ctx.Message.ClassifyModelVersion;
                    ctx.Saga.IndexedAt = ctx.Message.IndexedAt;
                }),

            // Edit arrived — discard all enrichment results and re-project from latest content.
            When(Classified, ctx => ctx.Saga.HasPendingEdit)
                .Then(ctx =>
                {
                    ctx.Saga.HasPendingEdit = false;
                    ctx.Saga.Tags = null;
                    ctx.Saga.ClassifyModelVersion = null;
                    ctx.Saga.Embedding = null;
                    ctx.Saga.EmbeddingModelVersion = null;
                    ctx.Saga.IndexedAt = null;
                })
                .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
                .TransitionTo(ProjectMessage.Pending),

            // No pending edit: publish enriched and settle.
            When(Classified, ctx => !ctx.Saga.HasPendingEdit)
                .PublishAsync(ctx => ctx.ToMessageEnriched())
                .Then(SettleSaga)
                .TransitionTo(Enriched),

            // Classify failure → Faulted with embedding preserved on saga state.
            When(ClassifyFault)
                .Then(SettleSaga)
                .Then(LogFault)
                .TransitionTo(Faulted));

        // ---------------------------------------------------------------------
        // Enriched: edits re-enter Project loop; *Invalidated re-runs Tag/Classify
        // ---------------------------------------------------------------------

        // Enriched is the steady state. Edits re-enter the full Project → Tag → Classify loop.
        // TagsInvalidated restarts from Tagging (both tag + classify re-run under new models).
        // ClassificationInvalidated skips tagging and jumps straight to Classifying (tag preserved).
        During(Enriched,
            When(MessageEditObserved)
                .Then(ctx =>
                {
                    ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                    ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                })
                .Request(ProjectMessage, ctx => ctx.Saga.ToProjectRequest())
                .TransitionTo(ProjectMessage.Pending),

            When(TagsInvalidated)
                .Publish(ctx => ctx.Saga.ToTagMessageRequested())
                .TransitionTo(Tagging),

            When(ClassificationInvalidated)
                .Publish(ctx => ctx.Saga.ToClassifyMessageRequested())
                .TransitionTo(Classifying));

        // ---------------------------------------------------------------------
        // Edit tracking: queue HasPendingEdit during in-flight requests
        // ---------------------------------------------------------------------

        // Edits during in-flight requests are queued via HasPendingEdit; each *.Completed handler
        // checks the flag and re-loops through ProjectMessage if set. Edits in Enriched are handled
        // above and trigger an immediate re-Request.
        During(
            [AnalyzeMessage.Pending, ProjectMessage.Pending, Tagging, Classifying],
            When(MessageEditObserved)
                .Then(ctx =>
                {
                    ctx.Saga.HasPendingEdit = true;
                    ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                    ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                }));

        // ---------------------------------------------------------------------
        // Replay from Faulted: phase-keyed re-entry to Tagging or Classifying
        // ---------------------------------------------------------------------

        // Two predicate branches on the same event. Each branch fires for the subset of
        // Faulted sagas matching its phase condition.
        //
        // Tag branch: saga has no embedding — tag/embedding phase failed, nothing stored yet.
        // Classify branch: saga has embedding but no tags — classify/LLM phase failed, embedding preserved.
        //
        // We key off saga state (Embedding/Tags) rather than RequestId nullability because
        // Tag/Classify no longer use Request<> and have no request IDs on the saga.
        During(Faulted,
            When(MessageReplayRequested, ctx => ctx.Saga.Embedding == null)
                .Publish(ctx => ctx.Saga.ToTagMessageRequested())
                .TransitionTo(Tagging),

            When(MessageReplayRequested, ctx => ctx.Saga.Embedding != null && ctx.Saga.Tags == null)
                .Publish(ctx => ctx.Saga.ToClassifyMessageRequested())
                .TransitionTo(Classifying));

        // ---------------------------------------------------------------------
        // Catch-all: bump UpdatedOn on every event in every non-Initial state
        // ---------------------------------------------------------------------

        // MassTransit runs every matching During() block — both the state-specific handler and
        // this catch-all fire for the same event. UpdateSaga only writes timestamp fields, so
        // there's no conflict with state-specific .Then() blocks that write other fields. Order
        // is registration order: this catch-all runs after the specific handler, which means
        // state-specific publishes see the prior event's UpdatedOn — fine since UpdateSaga
        // already ran on the previous event before this transition began.
        //
        // Initial is deliberately excluded. MT's MessageEventCorrelation._includesInitial check
        // selects NewOrExistingSagaPolicy for any event whose During() registration is reachable
        // from the Initial state. That policy calls MissingSagaPipe.Save → InsertOneAsync when
        // no instance is found. For timeout events (RequestTimeoutExpired<T>) that arrive after
        // the saga has already been completed and its RequestId field cleared, the ById query
        // returns nothing; the policy then tries to insert, hitting E11000 because the document
        // still exists at that _id (sagas are not deleted on completion, only transitioned).
        // Keeping Initial out of allStates means these events use the default DiscardPolicy on
        // miss instead. CreatedOn/UpdatedOn for the very first MessageCaptured are stamped by
        // the explicit .Then(UpdateSaga) in the Initially block above.
        State[] allStates =
        [
            AnalyzeMessage.Pending, ProjectMessage.Pending, Tagging, Classifying,
            Enriched, Excluded, Faulted,
        ];

        During(allStates,
            When(MessageCaptured).Then(UpdateSaga),
            When(MessageEditObserved).Then(UpdateSaga),
            When(MessageReplayRequested).Then(UpdateSaga),
            When(TagsInvalidated).Then(UpdateSaga),
            When(ClassificationInvalidated).Then(UpdateSaga),
            When(AnalyzeMessage.Completed).Then(UpdateSaga),
            When(AnalyzeMessage.Faulted).Then(UpdateSaga),
            When(AnalyzeMessage.TimeoutExpired).Then(UpdateSaga),
            When(ProjectMessage.Completed).Then(UpdateSaga),
            When(ProjectMessage.Faulted).Then(UpdateSaga),
            When(ProjectMessage.TimeoutExpired).Then(UpdateSaga),
            When(Tagged).Then(UpdateSaga),
            When(TagFault).Then(UpdateSaga),
            When(Classified).Then(UpdateSaga),
            When(ClassifyFault).Then(UpdateSaga));
    }

    // -------------------------------------------------------------------------
    // Fault log helpers
    // -------------------------------------------------------------------------

    private static void LogFault<TRequest>(BehaviorContext<MessageSagaState, Fault<TRequest>> ctx)
        where TRequest : class
        => LogContext.Error?.Log(
            "{Step} faulted for Snowflake={Snowflake}: {Exceptions}",
            typeof(TRequest).Name,
            ctx.Saga.MessageSnowflake,
            string.Join("; ", ctx.Message.Exceptions.Select(e => e.Message)));

    private static void LogTimeout<TRequest>(BehaviorContext<MessageSagaState, RequestTimeoutExpired<TRequest>> ctx)
        where TRequest : class
        => LogContext.Error?.Log(
            "{Step} timed out for Snowflake={Snowflake}",
            typeof(TRequest).Name,
            ctx.Saga.MessageSnowflake);

    // -------------------------------------------------------------------------
    // Timestamp helpers
    // -------------------------------------------------------------------------

    private static void UpdateSaga(BehaviorContext<MessageSagaState> ctx)
    {
        var now = ctx.SentTime ?? ctx.Saga.UpdatedOn;
        if (ctx.Saga.CreatedOn == default) ctx.Saga.CreatedOn = now;
        ctx.Saga.UpdatedOn = now;
    }

    private static void SettleSaga(BehaviorContext<MessageSagaState> ctx)
        => ctx.Saga.SettledOn = ctx.SentTime ?? ctx.Saga.UpdatedOn;

    // -------------------------------------------------------------------------
    // Saga construction helpers
    // -------------------------------------------------------------------------

    private static MessageSagaState BuildSagaFromCapture(MessageCaptured msg)
    {
        var saga = new MessageSagaState();
        CopyCaptureFields(saga, msg);
        return saga;
    }

    private static void CopyCaptureFields(MessageSagaState saga, MessageCaptured msg)
    {
        saga.CorrelationId = DeterministicGuid.FromSnowflake(msg.MessageSnowflake);
        saga.MessageId = saga.CorrelationId;
        saga.MessageSnowflake = msg.MessageSnowflake;
        saga.ChannelId = msg.ChannelId;
        saga.GuildId = msg.GuildId;
        saga.AuthorId = msg.AuthorId;
        saga.AuthorIsBot = msg.AuthorIsBot;
        saga.PayloadJson = msg.PayloadJson;
        saga.MessageCreatedAt = DecodeCreatedAt(msg.MessageSnowflake);
        // CreatedOn/UpdatedOn are stamped by the explicit .Then(UpdateSaga) in the Initially block.
    }

    private static DateTimeOffset DecodeCreatedAt(long snowflake) =>
        DateTimeOffset.FromUnixTimeMilliseconds((snowflake >> 22) + DiscordEpochMs);
}
