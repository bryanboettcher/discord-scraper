using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DiscordScraper.Write.Sagas;

/// <summary>
/// Orchestrates the message lifecycle: Analyze → Project → Enhance → Index → Enriched.
/// Edits arriving while a request is in flight are queued via HasPendingEdit and re-loop
/// through ProjectMessage on the next *.Completed handler. Edits in the Enriched terminal
/// state trigger an immediate re-loop.
/// </summary>
public sealed class MessageSagaStateMachine : MassTransitStateMachine<MessageSagaState>
{
    // 2015-01-01T00:00:00Z in Unix ms. Snowflake upper 42 bits encode (unixMs - epoch).
    // Inlined to avoid taking a project reference on DiscordScraper.Discord.
    private const long DiscordEpochMs = 1420070400000L;

    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _enhanceTimeout;

    // ReSharper disable UnassignedGetOnlyAutoProperty
    public State Excluded { get; private set; } = null!;
    public State Faulted { get; private set; } = null!;

    /// <summary>Terminal happy-path state. Saga persists indefinitely for edit re-loops.</summary>
    public State Enriched { get; private set; } = null!;

    public Event<MessageCaptured> MessageCaptured { get; private set; } = null!;
    public Event<MessageEditObserved> MessageEditObserved { get; private set; } = null!;
    public Event<ReEmbeddingRequested> ReEmbeddingRequested { get; private set; } = null!;
    public Event<ReTagRequested> ReTagRequested { get; private set; } = null!;

    public Request<MessageSagaState, AnalyzeMessageRequest, AnalyzeMessageResponse> AnalyzeMessage { get; private set; } = null!;
    public Request<MessageSagaState, ProjectMessageRequest, ProjectMessageResponse> ProjectMessage { get; private set; } = null!;
    public Request<MessageSagaState, EnhanceMessageRequest, EnhanceMessageResponse> EnhanceMessage { get; private set; } = null!;
    public Request<MessageSagaState, IndexMessageRequest, IndexMessageResponse> IndexMessage { get; private set; } = null!;

    /// <param name="requestTimeout">Override the default request timeout. Tests only; production uses defaults.</param>
    public MessageSagaStateMachine(
        ISystemClock clock,
        ILogger<MessageSagaStateMachine> logger,
        TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        // Ollama cold-start on first request can exceed 30s; EnhanceMessage gets extra budget.
        _enhanceTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);

        InstanceState(x => x.CurrentState);

        ConfigureEvents(clock);
        ConfigureRequests();
        ConfigureInitially(clock);
        ConfigureAnalyzePending(clock, logger);
        ConfigureProjectPending(clock, logger);
        ConfigureEnhancePending(clock, logger);
        ConfigureIndexPending(clock, logger);
        ConfigureEnriched(clock);
        ConfigureEditTracking();
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

        // CorrelateBy dispatches one event to every matching saga, serially. Same pattern as
        // GuildSagaStateMachine's SyncHeartbeat fan-out. The Cutoff prevents a saga that just
        // completed re-enrichment from immediately re-matching if its new model version hasn't
        // been written to the DB yet.
        Event(() => ReEmbeddingRequested, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.EmbeddingModelVersion != ctx.Message.ModelVersion
                && saga.LastUpdatedAt < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });

        Event(() => ReTagRequested, e =>
        {
            e.CorrelateBy((saga, ctx) =>
                saga.CurrentState == nameof(Enriched)
                && saga.TagModelVersion != ctx.Message.ModelVersion
                && saga.LastUpdatedAt < ctx.Message.Cutoff);
            e.OnMissingInstance(m => m.Discard());
        });
    }

    private void ConfigureRequests()
    {
        Request(() => AnalyzeMessage, x => x.AnalyzeMessageRequestId, r => r.Timeout = _requestTimeout);
        Request(() => ProjectMessage, x => x.ProjectMessageRequestId, r => r.Timeout = _requestTimeout);
        Request(() => EnhanceMessage, x => x.EnhanceMessageRequestId, r => r.Timeout = _enhanceTimeout);
        Request(() => IndexMessage, x => x.IndexMessageRequestId, r => r.Timeout = _requestTimeout);
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
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .IfElse(
                    ctx => ctx.Saga.IsSubstantive == true && ctx.Saga.IsBot != true,
                    binder => RequestProjectAndPark(binder),
                    binder => binder.TransitionTo(Excluded)),

            FaultedHandler(AnalyzeMessage, "AnalyzeMessage", clock, logger),
            TimeoutHandler(AnalyzeMessage, "AnalyzeMessage", clock, logger));
    }

    private void ConfigureProjectPending(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        During(ProjectMessage.Pending,
            When(ProjectMessage.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.IR = ctx.Message.IR;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .IfElse(
                    // Edit arrived during this in-flight request — re-project before enhancing
                    // so the embedding is based on the latest content.
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx => ctx.Saga.HasPendingEdit = false)),
                    binder => binder
                        .PublishAsync(ctx => ctx.Init<MessageProjected>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            ctx.Saga.LastUpdatedAt,
                            ctx.Saga.IR,
                        }))
                        .Request(EnhanceMessage, ctx => ctx.Init<EnhanceMessageRequest>(new
                        {
                            ctx.Saga.MessageSnowflake,
                            IR = ctx.Saga.IR!,
                            PlainText = IrTextFlattener.Flatten(ctx.Saga.IR!),
                        }))
                        .TransitionTo(EnhanceMessage.Pending)),

            FaultedHandler(ProjectMessage, "ProjectMessage", clock, logger),
            TimeoutHandler(ProjectMessage, "ProjectMessage", clock, logger));
    }

    private void ConfigureEnhancePending(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        During(EnhanceMessage.Pending,
            When(EnhanceMessage.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.Tags = ctx.Message.Tags;
                    ctx.Saga.Embedding = ctx.Message.Embedding.ToArray();
                    ctx.Saga.EmbeddingModelVersion = ctx.Message.EmbeddingModelVersion;
                    ctx.Saga.TagModelVersion = ctx.Message.TagModelVersion;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .IfElse(
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx => ctx.Saga.HasPendingEdit = false)),
                    binder => binder
                        .PublishAsync(ctx => ctx.Init<MessageEnhanced>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            ctx.Saga.LastUpdatedAt,
                            ctx.Saga.Tags,
                            Embedding = ctx.Saga.Embedding!,
                        }))
                        .Request(IndexMessage, ctx => ctx.Init<IndexMessageRequest>(new
                        {
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.GuildId,
                            ctx.Saga.ChannelId,
                            ctx.Saga.AuthorId,
                            CreatedAt = ctx.Saga.MessageCreatedAt,
                            Embedding = ctx.Saga.Embedding!,
                            Tags = ctx.Saga.Tags!,
                        }))
                        .TransitionTo(IndexMessage.Pending)),

            FaultedHandler(EnhanceMessage, "EnhanceMessage", clock, logger),
            TimeoutHandler(EnhanceMessage, "EnhanceMessage", clock, logger));
    }

    private void ConfigureIndexPending(ISystemClock clock, ILogger<MessageSagaStateMachine> logger)
    {
        During(IndexMessage.Pending,
            When(IndexMessage.Completed)
                .Then(ctx =>
                {
                    ctx.Saga.IndexedAt = ctx.Message.IndexedAt;
                    ctx.Saga.LastUpdatedAt = clock.UtcNow;
                })
                .IfElse(
                    ctx => ctx.Saga.HasPendingEdit,
                    binder => RequestProjectAndPark(binder.Then(ctx => ctx.Saga.HasPendingEdit = false)),
                    binder => binder
                        .PublishAsync(ctx => ctx.Init<MessageIndexed>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            ctx.Saga.LastUpdatedAt,
                            IndexedAt = ctx.Saga.IndexedAt!.Value,
                        }))
                        .PublishAsync(ctx => ctx.Init<MessageEnriched>(new
                        {
                            ctx.Saga.MessageId,
                            ctx.Saga.MessageSnowflake,
                            ctx.Saga.ChannelId,
                            ctx.Saga.GuildId,
                            ctx.Saga.AuthorId,
                            ctx.Saga.CurrentState,
                            ctx.Saga.LastUpdatedAt,
                            IR = ctx.Saga.IR!,
                            Tags = ctx.Saga.Tags!,
                            IsSubstantive = ctx.Saga.IsSubstantive!.Value,
                            IsBot = ctx.Saga.IsBot!.Value,
                            ctx.Saga.MessageCreatedAt,
                            ctx.Saga.EditedTimestamp,
                        }))
                        .TransitionTo(Enriched)),

            FaultedHandler(IndexMessage, "IndexMessage", clock, logger),
            TimeoutHandler(IndexMessage, "IndexMessage", clock, logger));
    }

    private void ConfigureEnriched(ISystemClock clock)
    {
        // Enriched is the steady state. Edits re-enter the full Project → Enhance → Index loop.
        // Re-enrichment events skip projection (content hasn't changed) and jump straight to
        // Enhance → Index, re-running both tagging and embedding under the new model.
        During(Enriched,
            RequestProjectAndPark(
                When(MessageEditObserved)
                    .Then(ctx =>
                    {
                        ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                        ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                        ctx.Saga.LastUpdatedAt = clock.UtcNow;
                    })),

            RequestEnhanceAndPark(
                When(ReEmbeddingRequested)
                    .Then(ctx => ctx.Saga.LastUpdatedAt = clock.UtcNow)),

            RequestEnhanceAndPark(
                When(ReTagRequested)
                    .Then(ctx => ctx.Saga.LastUpdatedAt = clock.UtcNow)));
    }

    private void ConfigureEditTracking()
    {
        // Edits during in-flight requests are queued via HasPendingEdit; each *.Completed handler
        // checks the flag and re-loops through ProjectMessage if set. Edits in Enriched are handled
        // in ConfigureEnriched and trigger an immediate re-Request.
        During(
            new[] { AnalyzeMessage.Pending, ProjectMessage.Pending, EnhanceMessage.Pending, IndexMessage.Pending },
            When(MessageEditObserved)
                .Then(ctx =>
                {
                    ctx.Saga.HasPendingEdit = true;
                    ctx.Saga.EditedTimestamp = ctx.Message.EditedAt;
                    ctx.Saga.PayloadJson = ctx.Message.UpdatedPayloadJson;
                }));
    }

    // -------------------------------------------------------------------------
    // Shared activity helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Issues a ProjectMessageRequest and transitions to ProjectMessage.Pending.
    /// Used from Initially → Analyze, the *.Completed re-loop branches, and the Enriched edit handler.
    /// </summary>
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

    /// <summary>
    /// Issues an EnhanceMessageRequest directly and transitions to EnhanceMessage.Pending.
    /// Used by re-enrichment fan-out events (ReEmbeddingRequested, ReTagRequested) when content
    /// hasn't changed — projection is skipped, but both tagging and embedding are re-run under
    /// the new model. The saga must already have IR populated (i.e. must be in Enriched state).
    /// </summary>
    private EventActivityBinder<MessageSagaState, T> RequestEnhanceAndPark<T>(
        EventActivityBinder<MessageSagaState, T> binder)
        where T : class =>
        binder
            .Request(EnhanceMessage, ctx => ctx.Init<EnhanceMessageRequest>(new
            {
                ctx.Saga.MessageSnowflake,
                IR = ctx.Saga.IR!,
                PlainText = IrTextFlattener.Flatten(ctx.Saga.IR!),
            }))
            .TransitionTo(EnhanceMessage.Pending);

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
                ctx.Saga.LastUpdatedAt = clock.UtcNow;
            })
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
                ctx.Saga.LastUpdatedAt = clock.UtcNow;
            })
            .TransitionTo(Faulted);

    private static MessageSagaState BuildSagaFromCapture(MessageCaptured msg, ISystemClock clock) =>
        new()
        {
            CorrelationId = DeterministicGuid.FromSnowflake(msg.MessageSnowflake),
            MessageId = DeterministicGuid.FromSnowflake(msg.MessageSnowflake),
            MessageSnowflake = msg.MessageSnowflake,
            ChannelId = msg.ChannelId,
            GuildId = msg.GuildId,
            AuthorId = msg.AuthorId,
            AuthorIsBot = msg.AuthorIsBot,
            PayloadJson = msg.PayloadJson,
            MessageCreatedAt = DecodeCreatedAt(msg.MessageSnowflake),
            LastUpdatedAt = clock.UtcNow,
        };

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
        saga.LastUpdatedAt = clock.UtcNow;
    }

    private static DateTimeOffset DecodeCreatedAt(long snowflake) =>
        DateTimeOffset.FromUnixTimeMilliseconds((snowflake >> 22) + DiscordEpochMs);
}
