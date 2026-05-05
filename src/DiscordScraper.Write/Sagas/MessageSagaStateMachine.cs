using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Orchestrates the message lifecycle:
/// Analyze → Project → Tagging → Classifying → Enriched.
///
/// Tag and Classify are sequential, saga-driven phases replacing the old parallel
/// Task.WhenAll EnhanceMessage that caused GPU thrashing on a shared Vulkan card.
///
/// Edits arriving while a request is in flight are queued via HasPendingEdit and re-loop
/// through ProjectMessage on the next *.Completed handler. Edits in the Enriched terminal
/// state trigger an immediate re-loop.
///
/// Replay: Faulted sagas receive MessageReplayRequested and re-enter Tagging or Classifying
/// based on which phase produced no results. ClearRequestIdOnFaulted is required so stale
/// RequestIds don't break correlation when re-firing from Faulted.
/// </summary>
public sealed class MessageSagaStateMachine : MassTransitStateMachine<MessageSagaState>
{
    // 2015-01-01T00:00:00Z in Unix ms. Snowflake upper 42 bits encode (unixMs - epoch).
    // Inlined to avoid taking a project reference on DiscordScraper.Discord.
    private const long DiscordEpochMs = 1420070400000L;

    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _tagTimeout;
    private readonly TimeSpan _classifyTimeout;

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Excluded { get; private set; } = null!;
    public State Faulted { get; private set; } = null!;

    /// <summary>Terminal happy-path state. Saga persists indefinitely for edit re-loops.</summary>
    public State Enriched { get; private set; } = null!;

    public State Tagging { get; private set; } = null!;
    public State Classifying { get; private set; } = null!;

    public Event<MessageCaptured> MessageCaptured { get; private set; } = null!;
    public Event<MessageEditObserved> MessageEditObserved { get; private set; } = null!;
    public Event<MessageReplayRequested> MessageReplayRequested { get; private set; } = null!;
    public Event<TagsInvalidated> TagsInvalidated { get; private set; } = null!;
    public Event<ClassificationInvalidated> ClassificationInvalidated { get; private set; } = null!;

    public Request<MessageSagaState, AnalyzeMessageRequest, AnalyzeMessageResponse> AnalyzeMessage { get; private set; } = null!;
    public Request<MessageSagaState, ProjectMessageRequest, ProjectMessageResponse> ProjectMessage { get; private set; } = null!;
    public Request<MessageSagaState, TagMessageRequest, TagMessageResponse> TagRequest { get; private set; } = null!;
    public Request<MessageSagaState, ClassifyMessageRequest, ClassifyMessageResponse> ClassifyRequest { get; private set; } = null!;

    /// <param name="requestTimeout">Override all request timeouts. Tests only; production resolves IOptions from DI.</param>
    public MessageSagaStateMachine(
        ISystemClock clock,
        ILogger<MessageSagaStateMachine> logger,
        IOptions<EnrichmentTagOptions>? tagOptions = null,
        IOptions<EnrichmentClassifyOptions>? classifyOptions = null,
        TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _tagTimeout = requestTimeout ?? tagOptions?.Value.RequestTimeout ?? TimeSpan.FromSeconds(30);
        _classifyTimeout = requestTimeout ?? classifyOptions?.Value.RequestTimeout ?? TimeSpan.FromSeconds(180);

        InstanceState(x => x.CurrentState);

        ConfigureEvents(clock);
        ConfigureRequests();
        ConfigureInitially(clock);
        ConfigureAnalyzePending(clock, logger);
        ConfigureProjectPending(clock, logger);
        ConfigureTagging(clock, logger);
        ConfigureClassifying(clock, logger);
        ConfigureEnriched(clock);
        ConfigureEditTracking();
        ConfigureReplayFromFaulted();

        // Catch-all: every event on any saga bumps UpdatedOn and initialises CreatedOn once.
        // MassTransit runs every matching During() block — both the state-specific handler and
        // this catch-all fire for the same event. UpdateSaga only writes timestamp fields, so
        // there's no conflict with state-specific .Then() blocks that write other fields. Order
        // is registration order: this catch-all runs after the specific handler, which means
        // state-specific publishes see the prior event's UpdatedOn — fine since UpdateSaga
        // already ran on the previous event before this transition began.
        State[] allStates =
        [
            Initial, AnalyzeMessage.Pending, ProjectMessage.Pending, Tagging, Classifying,
            Enriched, Excluded, Faulted,
        ];

        During(allStates,
            When(MessageCaptured).Then(ctx => UpdateSaga(ctx, clock)),
            When(MessageEditObserved).Then(ctx => UpdateSaga(ctx, clock)),
            When(MessageReplayRequested).Then(ctx => UpdateSaga(ctx, clock)),
            When(TagsInvalidated).Then(ctx => UpdateSaga(ctx, clock)),
            When(ClassificationInvalidated).Then(ctx => UpdateSaga(ctx, clock)),
            When(AnalyzeMessage.Completed).Then(ctx => UpdateSaga(ctx, clock)),
            When(AnalyzeMessage.Faulted).Then(ctx => UpdateSaga(ctx, clock)),
            When(AnalyzeMessage.TimeoutExpired).Then(ctx => UpdateSaga(ctx, clock)),
            When(ProjectMessage.Completed).Then(ctx => UpdateSaga(ctx, clock)),
            When(ProjectMessage.Faulted).Then(ctx => UpdateSaga(ctx, clock)),
            When(ProjectMessage.TimeoutExpired).Then(ctx => UpdateSaga(ctx, clock)),
            When(TagRequest.Completed).Then(ctx => UpdateSaga(ctx, clock)),
            When(TagRequest.Faulted).Then(ctx => UpdateSaga(ctx, clock)),
            When(TagRequest.TimeoutExpired).Then(ctx => UpdateSaga(ctx, clock)),
            When(ClassifyRequest.Completed).Then(ctx => UpdateSaga(ctx, clock)),
            When(ClassifyRequest.Faulted).Then(ctx => UpdateSaga(ctx, clock)),
            When(ClassifyRequest.TimeoutExpired).Then(ctx => UpdateSaga(ctx, clock)));
    }

    private void ConfigureEvents(ISystemClock clock)
    {
        Event(() => MessageCaptured, e =>
        {
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake));
            e.InsertOnInitial = true;
            e.SetSagaFactory(ctx => BuildSagaFromCapture(ctx.Message, clock));
        });

        Event(() => MessageEditObserved, e =>
            e.CorrelateById(ctx => DeterministicGuid.FromSnowflake(ctx.Message.MessageSnowflake)));

        // Fan-out to all Faulted sagas matching the requested phase filter.
        Event(() => MessageReplayRequested, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Faulted)
                && (ctx.Message.Phase == null || PhaseMatches(saga, ctx.Message.Phase)));
            e.OnMissingInstance(m => m.Discard());
        });

        // Fan-out for re-tagging under a new embedding model. Re-runs both Tag (embedding)
        // and Classify so downstream vector points and LLM tags stay consistent.
        Event(() => TagsInvalidated, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.EmbeddingModelVersion != ctx.Message.ModelVersion
                && saga.UpdatedOn < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });

        // Fan-out for re-classification under a new LLM model. Embedding (Tag phase) is preserved;
        // only Classify is re-run.
        Event(() => ClassificationInvalidated, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.ClassifyModelVersion != ctx.Message.ModelVersion
                && saga.UpdatedOn < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });
    }

    private void ConfigureRequests()
    {
        Request(() => AnalyzeMessage, x => x.AnalyzeMessageRequestId, r => r.Timeout = _requestTimeout);
        Request(() => ProjectMessage, x => x.ProjectMessageRequestId, r => r.Timeout = _requestTimeout);

        Request(() => TagRequest, x => x.TagRequestId, r =>
        {
            r.Timeout = _tagTimeout;
            // Required for replay: without this a Faulted saga retains the stale RequestId
            // and the new TagRequest fires with a different Id, breaking response correlation.
            r.ClearRequestIdOnFaulted = true;
        });

        Request(() => ClassifyRequest, x => x.ClassifyRequestId, r =>
        {
            r.Timeout = _classifyTimeout;
            r.ClearRequestIdOnFaulted = true;
        });
    }

    private void ConfigureInitially(ISystemClock clock)
    {
        Initially(
            When(MessageCaptured)
                .Then(ctx => CopyCaptureFields(ctx.Saga, ctx.Message, clock))
                .Request(AnalyzeMessage, ctx => ctx.Init<AnalyzeMessageRequest>(new
                {
                    ctx.Saga.MessageSnowflake,
                    ctx.Saga.PayloadJson,
                    ctx.Saga.AuthorIsBot,
                }))
                .TransitionTo(AnalyzeMessage.Pending));
    }

    private void ConfigureAnalyzePending(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        During(AnalyzeMessage.Pending,
            When(AnalyzeMessage.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.IsSubstantive = ctx.Message.IsSubstantive;
                    ctx.Saga.IsBot = ctx.Message.IsBot;
                    ctx.Saga.DetectedLanguage = ctx.Message.DetectedLanguage;
                })
                .PublishAsync(ctx => ctx.Init<MessageAnalyzed>(new
                {
                    ctx.Saga.MessageId,
                    ctx.Saga.MessageSnowflake,
                    ctx.Saga.ChannelId,
                    ctx.Saga.GuildId,
                    ctx.Saga.AuthorId,
                    ctx.Saga.CurrentState,
                    UpdatedOn = ctx.Saga.UpdatedOn,
                    IsSubstantive = ctx.Saga.IsSubstantive!.Value,
                    IsBot = ctx.Saga.IsBot!.Value,
                    ctx.Saga.DetectedLanguage,
                }))
                .IfElse(
                    ctx => ctx.Saga.IsSubstantive == true && ctx.Saga.IsBot != true,
                    binder => RequestProjectAndPark(binder),
                    binder => binder
                        .Then(ctx => SettleSaga(ctx, clock))
                        .TransitionTo(Excluded)),

            FaultedHandler(AnalyzeMessage, "AnalyzeMessage", clock, logger),
            TimeoutHandler(AnalyzeMessage, "AnalyzeMessage", clock, logger));
    }

    private void ConfigureProjectPending(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        During(ProjectMessage.Pending,
            When(ProjectMessage.Completed)
                .Then(ctx => ctx.Saga.IR = ctx.Message.IR)
                .IfElse(
                    // Edit arrived during this in-flight request — re-project before tagging
                    // so the embedding is based on the latest content.
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx => ctx.Saga.HasPendingEdit = false)),
                    binder => RequestTagAndPark(binder
                        .PublishAsync(ctx => ctx.Init<MessageProjected>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            UpdatedOn = ctx.Saga.UpdatedOn,
                            ctx.Saga.IR,
                        }))
                        .Then(ctx =>
                        {
                            // Clear Tag/Classify results before re-entering Tagging so a
                            // re-project always produces fresh enrichment.
                            ctx.Saga.Tags = null;
                            ctx.Saga.ClassifyModelVersion = null;
                            ctx.Saga.Embedding = null;
                            ctx.Saga.EmbeddingModelVersion = null;
                            ctx.Saga.IndexedAt = null;
                        }))),

            FaultedHandler(ProjectMessage, "ProjectMessage", clock, logger),
            TimeoutHandler(ProjectMessage, "ProjectMessage", clock, logger));
    }

    private void ConfigureTagging(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        // Tag phase = embedding (nomic-embed-text). On success: embedding vector stored, then Classify fires.
        During(Tagging,
            When(TagRequest.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.Embedding = ctx.Message.Embedding.ToArray();
                    ctx.Saga.EmbeddingModelVersion = ctx.Message.EmbeddingModelVersion;
                })
                .IfElse(
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx =>
                    {
                        ctx.Saga.HasPendingEdit = false;
                        ctx.Saga.Embedding = null;
                        ctx.Saga.EmbeddingModelVersion = null;
                    })),
                    binder => RequestClassifyAndPark(binder
                        .PublishAsync(ctx => ctx.Init<MessageTagged>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            UpdatedOn = ctx.Saga.UpdatedOn,
                            Embedding = ctx.Saga.Embedding!,
                            EmbeddingModelVersion = ctx.Saga.EmbeddingModelVersion!,
                        })))),

            // Tag failure → Faulted with no embedding stored.
            FaultedHandler(TagRequest, "TagRequest", clock, logger),
            TimeoutHandler(TagRequest, "TagRequest", clock, logger));
    }

    private void ConfigureClassifying(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        // Classify phase = LLM topic tagging + vector store indexing. On success: tags + IndexedAt stored.
        During(Classifying,
            When(ClassifyRequest.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.Tags = ctx.Message.Tags;
                    ctx.Saga.ClassifyModelVersion = ctx.Message.ClassifyModelVersion;
                    ctx.Saga.IndexedAt = ctx.Message.IndexedAt;
                })
                .IfElse(
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx =>
                    {
                        ctx.Saga.HasPendingEdit = false;
                        ctx.Saga.Tags = null;
                        ctx.Saga.ClassifyModelVersion = null;
                        ctx.Saga.Embedding = null;
                        ctx.Saga.EmbeddingModelVersion = null;
                        ctx.Saga.IndexedAt = null;
                    })),
                    binder => binder
                        .PublishAsync(ctx => ctx.Init<MessageClassified>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            UpdatedOn = ctx.Saga.UpdatedOn,
                            Tags = ctx.Saga.Tags!,
                            ClassifyModelVersion = ctx.Saga.ClassifyModelVersion!,
                        }))
                        .PublishAsync(ctx => ctx.Init<MessageEnriched>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            UpdatedOn = ctx.Saga.UpdatedOn,
                            IR = ctx.Saga.IR!,
                            Tags = ctx.Saga.Tags!,
                            IsSubstantive = ctx.Saga.IsSubstantive!.Value,
                            IsBot = ctx.Saga.IsBot!.Value,
                            ctx.Saga.MessageCreatedAt,
                            ctx.Saga.EditedTimestamp,
                        }))
                        .Then(ctx => SettleSaga(ctx, clock))
                        .TransitionTo(Enriched)),

            // Classify failure → Faulted with embedding preserved on saga state.
            FaultedHandler(ClassifyRequest, "ClassifyRequest", clock, logger),
            TimeoutHandler(ClassifyRequest, "ClassifyRequest", clock, logger));
    }

    private void ConfigureEnriched(ISystemClock clock)
    {
        // Enriched is the steady state. Edits re-enter the full Project → Tag → Classify loop.
        // TagsInvalidated restarts from Tagging (both tag + classify re-run under new models).
        // ClassificationInvalidated skips tagging and jumps straight to Classifying (tag preserved).
        During(Enriched,
            RequestProjectAndPark(
                When(MessageEditObserved)
                    .Then(ctx =>
                    {
                        ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                        ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                    })),

            RequestTagAndPark(When(TagsInvalidated)),

            RequestClassifyAndPark(When(ClassificationInvalidated)));
    }

    private void ConfigureEditTracking()
    {
        // Edits during in-flight requests are queued via HasPendingEdit; each *.Completed handler
        // checks the flag and re-loops through ProjectMessage if set. Edits in Enriched are handled
        // in ConfigureEnriched and trigger an immediate re-Request.
        During(
            new[] { AnalyzeMessage.Pending, ProjectMessage.Pending, Tagging, Classifying },
            When(MessageEditObserved)
                .Then(ctx =>
                {
                    ctx.Saga.HasPendingEdit = true;
                    ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                    ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                }));
    }

    private void ConfigureReplayFromFaulted()
    {
        // Two predicate branches on the same event. Each branch fires for the subset of
        // Faulted sagas matching its phase condition.
        //
        // Tag branch: saga has no embedding — tag/embedding phase failed, nothing stored yet.
        // Classify branch: saga has embedding but no tags — classify/LLM phase failed, embedding preserved.
        //
        // We key off saga state (Embedding/Tags) rather than RequestId nullability because
        // ClearRequestIdOnFaulted on the Request<> declarations already nulls those — leaning on
        // the actual phase output is the clearer invariant.
        During(Faulted,
            RequestTagAndPark(
                When(MessageReplayRequested, ctx => ctx.Saga.Embedding == null)
                    .Then(ctx =>
                    {
                        // ClearRequestIdOnFaulted already nulled these, but be explicit so replay
                        // always starts with clean request correlation state.
                        ctx.Saga.TagRequestId = null;
                        ctx.Saga.ClassifyRequestId = null;
                    })),

            RequestClassifyAndPark(
                When(MessageReplayRequested, ctx => ctx.Saga.Embedding != null && ctx.Saga.Tags == null)
                    .Then(ctx => ctx.Saga.ClassifyRequestId = null)));
    }

    // -------------------------------------------------------------------------
    // Timestamp helpers
    // -------------------------------------------------------------------------

    private static void UpdateSaga(BehaviorContext<MessageSagaState> ctx, ISystemClock clock)
    {
        var now = clock.UtcNow;
        if (ctx.Saga.CreatedOn == default) ctx.Saga.CreatedOn = now;
        ctx.Saga.UpdatedOn = now;
    }

    private static void SettleSaga(BehaviorContext<MessageSagaState> ctx, ISystemClock clock)
        => ctx.Saga.SettledOn = clock.UtcNow;

    // -------------------------------------------------------------------------
    // Shared activity helpers
    // -------------------------------------------------------------------------

    private EventActivityBinder<MessageSagaState, T> RequestProjectAndPark<T>(
        EventActivityBinder<MessageSagaState, T> binder)
        where T : class =>
        binder
            .Request(ProjectMessage, ctx => ctx.Init<ProjectMessageRequest>(new
            {
                ctx.Saga.MessageSnowflake,
                ctx.Saga.ChannelId,
                ctx.Saga.GuildId,
                ctx.Saga.PayloadJson,
            }))
            .TransitionTo(ProjectMessage.Pending);

    private EventActivityBinder<MessageSagaState, T> RequestTagAndPark<T>(
        EventActivityBinder<MessageSagaState, T> binder)
        where T : class =>
        binder
            .Request(TagRequest, ctx => ctx.Init<TagMessageRequest>(new
            {
                ctx.Saga.MessageSnowflake,
                PlainText = IrTextFlattener.Flatten(ctx.Saga.IR!),
            }))
            .TransitionTo(Tagging);

    private EventActivityBinder<MessageSagaState, T> RequestClassifyAndPark<T>(
        EventActivityBinder<MessageSagaState, T> binder)
        where T : class =>
        binder
            .Request(ClassifyRequest, ctx => ctx.Init<ClassifyMessageRequest>(new
            {
                ctx.Saga.MessageSnowflake,
                ctx.Saga.GuildId,
                ctx.Saga.ChannelId,
                ctx.Saga.AuthorId,
                CreatedAt = ctx.Saga.MessageCreatedAt,
                PlainText = IrTextFlattener.Flatten(ctx.Saga.IR!),
                Embedding = ctx.Saga.Embedding!,
            }))
            .TransitionTo(Classifying);

    private EventActivities<MessageSagaState> FaultedHandler<TRequest, TResponse>(
        Request<MessageSagaState, TRequest, TResponse> request,
        string label,
        ISystemClock clock,
        ILogger<MessageSagaStateMachine> logger)
        where TRequest : class
        where TResponse : class =>
        When(request.Faulted)
            .Then(ctx =>
            {
                logger.LogError(
                    "{Step} faulted for MessageSnowflake={Snowflake}: {Exceptions}",
                    label,
                    ctx.Saga.MessageSnowflake,
                    string.Join("; ", ctx.Message.Exceptions.Select(e => e.Message)));
            })
            .Then(ctx => SettleSaga(ctx, clock))
            .TransitionTo(Faulted);

    private EventActivities<MessageSagaState> TimeoutHandler<TRequest, TResponse>(
        Request<MessageSagaState, TRequest, TResponse> request,
        string label,
        ISystemClock clock,
        ILogger<MessageSagaStateMachine> logger)
        where TRequest : class
        where TResponse : class =>
        When(request.TimeoutExpired)
            .Then(ctx =>
            {
                logger.LogError(
                    "{Step} timed out for MessageSnowflake={Snowflake}",
                    label,
                    ctx.Saga.MessageSnowflake);
            })
            .Then(ctx => SettleSaga(ctx, clock))
            .TransitionTo(Faulted);

    /// <summary>
    /// Returns true if the saga's fault phase matches the requested phase filter.
    /// "tag"      — no embedding stored (tag/embedding phase faulted).
    /// "classify" — embedding present but no tags (classify/LLM phase faulted).
    /// null/other — matches both.
    /// </summary>
    private static bool PhaseMatches(MessageSagaState saga, string phase) =>
        phase switch
        {
            "tag"      => saga.Embedding == null,
            "classify" => saga.Embedding != null && saga.Tags == null,
            _          => true,
        };

    private static MessageSagaState BuildSagaFromCapture(MessageCaptured msg, ISystemClock clock)
    {
        var saga = new MessageSagaState();
        CopyCaptureFields(saga, msg, clock);
        return saga;
    }

    private static void CopyCaptureFields(MessageSagaState saga, MessageCaptured msg, ISystemClock clock)
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
        // CreatedOn/UpdatedOn are set by the catch-all UpdateSaga after this factory runs.
    }

    private static DateTimeOffset DecodeCreatedAt(long snowflake) =>
        DateTimeOffset.FromUnixTimeMilliseconds((snowflake >> 22) + DiscordEpochMs);
}
