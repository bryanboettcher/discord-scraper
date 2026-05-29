using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Events.Sync;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Contracts;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.Extensions;

namespace DiscordScraper.Write.Tests.Sagas;

[TestFixture]
public sealed class MessageSagaStateMachineTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedIndexedAt = new(2026, 1, 15, 12, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<string> StubTags = ["dotnet", "csharp"];
    private static readonly IReadOnlyList<float> StubEmbedding = [0.1f, 0.2f, 0.3f];
    private const string StubEmbeddingModel = "nomic-embed-text";
    private const string StubTagModel = "llama3.1:8b";

    private static readonly MessageIR StubIR = new(
        Body: new[] { new TextNode("hello") },
        Attachments: Array.Empty<AttachmentIR>(),
        Embeds: Array.Empty<EmbedIR>(),
        ReplyTo: null,
        CapturedAt: FixedNow);

    private static ISystemClock MakeClock() =>
        Substitute.For<ISystemClock>().With(c => c.UtcNow.Returns(FixedNow));

    // Builds an in-memory harness wired through the full happy path:
    // Analyze → Project → Tagging → Classifying → Enriched.
    private static ServiceProvider BuildProvider(
        ISystemClock clock,
        bool isSubstantive = true,
        bool isBot = false,
        string detectedLanguage = "en",
        MessageIR? projectIR = null)
    {
        var ir = projectIR ?? StubIR;
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = isSubstantive,
                        IsBot = isBot,
                        DetectedLanguage = detectedLanguage,
                    });
                });

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new ProjectMessageResponse(ir));
                });

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);
    }

    // Provider that stubs Analyze but never responds to ProjectMessage — saga parks in ProjectMessage.Pending.
    private static ServiceProvider BuildProviderProjectPending(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });
                // No ProjectMessage handler — saga stays in ProjectMessage.Pending
            })
            .BuildServiceProvider(true);
    }

    // Provider that stubs through Project but parks at Tagging (no TagMessageRequested handler).
    private static ServiceProvider BuildProviderTagPending(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });
                // No TagMessageRequested handler — no MessageTagged published, saga stays in Tagging.
            })
            .BuildServiceProvider(true);
    }

    // Provider that stubs through Tag but parks at Classifying (no ClassifyMessageRequested handler).
    private static ServiceProvider BuildProviderClassifyPending(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });
                // No ClassifyMessageRequested handler — no MessageClassified published, saga stays in Classifying.
            })
            .BuildServiceProvider(true);
    }

    private static object BuildMessageCaptured(long snowflake) => new
    {
        MessageSnowflake = snowflake,
        ChannelId = 1000L,
        GuildId = 2000L,
        AuthorId = 3000L,
        AuthorIsBot = false,
        PayloadJson = """{"id":"123","content":"hello"}""",
        CurrentState = "Initial",
        UpdatedOn = FixedNow,
        HomeChannelName = (string?)null,
    };

    private static object BuildMessageEditObserved(long snowflake, DateTimeOffset? editedAt = null) => new
    {
        MessageSnowflake = snowflake,
        ChannelId = 1000L,
        GuildId = 2000L,
        AuthorId = 3000L,
        EditedAt = editedAt ?? FixedNow.AddHours(1),
        UpdatedPayloadJson = """{"id":"123","content":"edited"}""",
        CurrentState = "Projected",
        UpdatedOn = FixedNow,
    };

    // -------------------------------------------------------------------------
    // Non-substantive → Excluded
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageCaptured_with_non_substantive_reaches_Excluded()
    {
        var clock = MakeClock();
        await using var provider = BuildProvider(clock, isSubstantive: false, isBot: false);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 200000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Excluded);

        sagaId.ShouldNotBeNull("Saga did not reach Excluded state for non-substantive message");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.IsSubstantive.ShouldBe(false);
    }

    // -------------------------------------------------------------------------
    // Bot message → Excluded even when substantive
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageCaptured_with_bot_true_reaches_Excluded()
    {
        var clock = MakeClock();
        // Substantive=true but bot=true → should still be Excluded
        await using var provider = BuildProvider(clock, isSubstantive: true, isBot: true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 300000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Excluded);

        sagaId.ShouldNotBeNull("Saga did not reach Excluded state for bot message");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.IsBot.ShouldBe(true);
    }

    // -------------------------------------------------------------------------
    // EditObserved during AnalyzeMessage.Pending → HasPendingEdit=true
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_AnalyzePending_sets_HasPendingEdit()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();
                // No AnalyzeMessage handler — saga stays in AnalyzeMessage.Pending
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 400000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.AnalyzeMessage.Pending);

        var editedAt = new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero);
        await harness.Bus.Publish<MessageEditObserved>(new
        {
            MessageSnowflake = snowflake,
            ChannelId = 1000L,
            GuildId = 2000L,
            AuthorId = 3000L,
            EditedAt = editedAt,
            UpdatedPayloadJson = """{"id":"123","content":"edited"}""",
            CurrentState = "AnalyzeMessage_Pending",
            UpdatedOn = FixedNow,
        });

        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeTrue("HasPendingEdit should be set when edit arrives during AnalyzePending");
        saga.EditedTimestamp.ShouldBe(editedAt);
    }

    // -------------------------------------------------------------------------
    // Duplicate MessageCaptured for same snowflake hits the same saga (idempotent)
    // -------------------------------------------------------------------------

    [Test]
    public async Task Duplicate_MessageCaptured_same_snowflake_hits_same_saga()
    {
        var clock = MakeClock();
        await using var provider = BuildProvider(clock, isSubstantive: true, isBot: false);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 500000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        var msg = BuildMessageCaptured(snowflake);
        await harness.Bus.Publish<MessageCaptured>(msg);
        await harness.Bus.Publish<MessageCaptured>(msg);

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        // Full pipeline now routes to Enriched, not Projected.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga did not reach Enriched after duplicate MessageCaptured");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull("Saga instance should exist for the snowflake after duplicate publish");
    }

    // -------------------------------------------------------------------------
    // CorrelationId == DeterministicGuid.FromSnowflake
    // -------------------------------------------------------------------------

    [Test]
    public async Task CorrelationId_matches_deterministic_guid_from_snowflake()
    {
        var clock = MakeClock();
        await using var provider = BuildProvider(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 600000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.CorrelationId.ShouldBe(expectedId);
        saga.MessageId.ShouldBe(expectedId);
    }

    // =========================================================================
    // Full pipeline + edit re-loop tests
    // =========================================================================

    // -------------------------------------------------------------------------
    // Substantive non-bot → reaches Enriched with IR + Tags + Embedding + IndexedAt populated
    // -------------------------------------------------------------------------

    [Test]
    public async Task Substantive_non_bot_reaches_Enriched_with_all_fields_populated()
    {
        var clock = MakeClock();
        await using var provider = BuildProvider(clock, isSubstantive: true, isBot: false);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 700000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        sagaId.ShouldNotBeNull("Saga did not reach Enriched state");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.IR.ShouldNotBeNull("IR should be populated after ProjectMessage.Completed");
        saga.Tags.ShouldNotBeNull("Tags should be populated after EnhanceMessage.Completed");
        saga.Embedding.ShouldNotBeNull("Embedding should be populated after EnhanceMessage.Completed");
        saga.IndexedAt.ShouldNotBeNull("IndexedAt should be populated after IndexMessage.Completed");
        saga.IndexedAt!.Value.ShouldBe(FixedIndexedAt);
        saga.IsSubstantive.ShouldBe(true);
        saga.IsBot.ShouldBe(false);
        saga.DetectedLanguage.ShouldBe("en");
        saga.MessageSnowflake.ShouldBe(snowflake);
    }

    // -------------------------------------------------------------------------
    // Test 7: ProjectMessage.Faulted → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task ProjectMessage_Faulted_transitions_to_Faulted()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });

                // Fault the ProjectMessage request — MT treats a thrown exception as a fault.
                cfg.AddHandler<ProjectMessageRequest>((Func<ConsumeContext<ProjectMessageRequest>, Task>)(_ =>
                    throw new InvalidOperationException("projection exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 710000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));

        sagaId.ShouldNotBeNull("Saga should reach Faulted when ProjectMessage consumer throws");
    }

    // -------------------------------------------------------------------------
    // Test 8: ProjectMessage.TimeoutExpired → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task ProjectMessage_TimeoutExpired_transitions_to_Faulted()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderProjectPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 720000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        // Wait for ProjectMessage.Pending (Analyze stub responded, Project has no responder).
        await sagaHarness.Exists(expectedId, machine.ProjectMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        // Retrieve the in-flight request ID to synthesize the timeout event.
        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        var requestId = saga.ProjectMessageRequestId;
        requestId.ShouldNotBeNull("ProjectMessageRequestId must be set while in Pending");

        // Send RequestTimeoutExpired directly — bypasses the real scheduler.
        await harness.Bus.Publish<RequestTimeoutExpired<ProjectMessageRequest>>(new
        {
            RequestId = requestId.Value,
            CorrelationId = expectedId,
            Timestamp = DateTime.UtcNow,
            ExpirationTime = DateTime.UtcNow,
        });

        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(5));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when RequestTimeoutExpired arrives");
    }

    // -------------------------------------------------------------------------
    // Test 9: EditObserved during ProjectMessage.Pending → HasPendingEdit=true
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_ProjectPending_sets_HasPendingEdit()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderProjectPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 730000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.ProjectMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        var editedAt = FixedNow.AddHours(2);
        await harness.Bus.Publish<MessageEditObserved>(new
        {
            MessageSnowflake = snowflake,
            ChannelId = 1000L,
            GuildId = 2000L,
            AuthorId = 3000L,
            EditedAt = editedAt,
            UpdatedPayloadJson = """{"id":"123","content":"edited again"}""",
            CurrentState = "ProjectMessage_Pending",
            UpdatedOn = FixedNow,
        });

        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeTrue("HasPendingEdit should be set when edit arrives during ProjectPending");
        saga.EditedTimestamp.ShouldBe(editedAt);
    }

    // -------------------------------------------------------------------------
    // Test 10: HasPendingEdit re-loop — edit during ProjectMessage.Pending triggers second Request
    // -------------------------------------------------------------------------

    [Test]
    public async Task HasPendingEdit_during_ProjectPending_triggers_second_ProjectMessage_request()
    {
        var clock = MakeClock();

        var firstRequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    var n = Interlocked.Increment(ref requestCount);
                    if (n == 1)
                        await firstRequestGate.Task;
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });

                // Tag + Classify stubs so saga can complete after the re-loop.
                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 740000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.ProjectMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake));
        await Task.Delay(200);

        firstRequestGate.SetResult();

        // After re-loop: saga processes second ProjectMessage, then Enhance, then Index → Enriched.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Enriched after the HasPendingEdit re-loop");

        requestCount.ShouldBe(2, "Exactly two ProjectMessage requests should have been issued");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeFalse("HasPendingEdit should be cleared after re-loop completes");
        saga.IR.ShouldNotBeNull();
    }

    // -------------------------------------------------------------------------
    // Test 11: EditObserved during Enriched → re-enters ProjectMessage.Pending → Enriched
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_Enriched_triggers_full_reprojection_to_Enriched()
    {
        var clock = MakeClock();
        var projectCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true,
                        IsBot = false,
                        DetectedLanguage = "en",
                    });
                });

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    Interlocked.Increment(ref projectCount);
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 750000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        projectCount.ShouldBe(1, "One ProjectMessage request for the initial enrichment");

        var editedAt = FixedNow.AddHours(3);
        await harness.Bus.Publish<MessageEditObserved>(new
        {
            MessageSnowflake = snowflake,
            ChannelId = 1000L,
            GuildId = 2000L,
            AuthorId = 3000L,
            EditedAt = editedAt,
            UpdatedPayloadJson = """{"id":"123","content":"re-edited"}""",
            CurrentState = "Enriched",
            UpdatedOn = FixedNow,
        });

        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.ProjectMessage.Pending, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        projectCount.ShouldBe(2, "Two ProjectMessage requests total after edit re-loop");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.EditedTimestamp.ShouldBe(editedAt);
        saga.IR.ShouldNotBeNull();
        saga.IndexedAt.ShouldNotBeNull();
    }

    // =========================================================================
    // Edit re-loop tests during Tagging and Classifying states
    // =========================================================================

    // -------------------------------------------------------------------------
    // Test 12: Fault<TagMessageRequested> → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task TagFault_transitions_to_Faulted()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                // Consumer throws — MT auto-publishes Fault<TagMessageRequested>.
                cfg.AddHandler<TagMessageRequested>((Func<ConsumeContext<TagMessageRequested>, Task>)(_ =>
                    throw new InvalidOperationException("embedding exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 760000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when TagMessageRequested consumer throws");
    }

    // -------------------------------------------------------------------------
    // Test 13: Fault<ClassifyMessageRequested> → Faulted (embedding preserved)
    //          Renamed from TimeoutExpired — timeouts are no longer saga-managed.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassifyFault_transitions_to_Faulted_with_embedding_preserved()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                // Consumer throws — MT auto-publishes Fault<ClassifyMessageRequested>.
                cfg.AddHandler<ClassifyMessageRequested>((Func<ConsumeContext<ClassifyMessageRequested>, Task>)(_ =>
                    throw new InvalidOperationException("llm exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 770000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when ClassifyMessageRequested consumer throws");

        // Embedding must be preserved when Classify faults — replay can skip re-embedding.
        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.Embedding.ShouldNotBeNull("Embedding must be preserved after Classify fault");
        saga.Tags.ShouldBeNull("Tags must not be set when Classify faulted");
    }

    // -------------------------------------------------------------------------
    // Test 14: ClassifyRequest.Faulted → Faulted (embedding preserved)
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassifyRequest_Faulted_transitions_to_Faulted_with_embedding_preserved()
    {
        var clock = MakeClock();
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>((Func<ConsumeContext<ClassifyMessageRequested>, Task>)(_ =>
                    throw new InvalidOperationException("llm exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 771000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when ClassifyRequest consumer throws");

        // Embedding must be preserved when Classify fails — replay can skip re-embedding.
        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.Embedding.ShouldNotBeNull("Embedding must be preserved after Classify fault");
        saga.Tags.ShouldBeNull("Tags must not be set when Classify faulted");
    }

    // -------------------------------------------------------------------------
    // Test 16: EditObserved during EnhanceMessage.Pending → HasPendingEdit=true
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_EnhancePending_sets_HasPendingEdit()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderTagPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 780000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.Tagging, timeout: TimeSpan.FromSeconds(5));

        var editedAt = FixedNow.AddHours(2);
        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake, editedAt));
        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeTrue("HasPendingEdit should be set when edit arrives during EnhancePending");
        saga.EditedTimestamp.ShouldBe(editedAt);
    }

    // -------------------------------------------------------------------------
    // Test 17: EditObserved during IndexMessage.Pending → HasPendingEdit=true
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_IndexPending_sets_HasPendingEdit()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderClassifyPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 781000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.Classifying, timeout: TimeSpan.FromSeconds(5));

        var editedAt = FixedNow.AddHours(2);
        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake, editedAt));
        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeTrue("HasPendingEdit should be set when edit arrives during IndexPending");
        saga.EditedTimestamp.ShouldBe(editedAt);
    }

    // -------------------------------------------------------------------------
    // Test 18: HasPendingEdit re-loop after TagRequest.Completed → re-Request(ProjectMessage)
    // -------------------------------------------------------------------------

    [Test]
    public async Task HasPendingEdit_during_Tagging_triggers_re_ProjectMessage()
    {
        var clock = MakeClock();

        var tagGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    Interlocked.Increment(ref projectCount);
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    // Block until the test has published an edit — ensures HasPendingEdit is
                    // set before the Tagged event fires.
                    await tagGate.Task;
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    }));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 790000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.Tagging, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake));
        await Task.Delay(200);

        tagGate.SetResult();

        // After gate release: TagRequest.Completed fires, sees HasPendingEdit, loops back to ProjectMessage.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Enriched after HasPendingEdit re-loop from Tagging");

        // projectCount == 2: initial + re-loop
        projectCount.ShouldBe(2, "Two ProjectMessage requests: initial and re-loop after pending edit");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeFalse("HasPendingEdit cleared after re-loop");
    }

    // -------------------------------------------------------------------------
    // Test 19: HasPendingEdit re-loop after ClassifyRequest.Completed → re-Request(ProjectMessage)
    // -------------------------------------------------------------------------

    [Test]
    public async Task HasPendingEdit_during_Classifying_triggers_re_ProjectMessage()
    {
        var clock = MakeClock();

        var classifyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                {
                    Interlocked.Increment(ref projectCount);
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR));
                });

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    await classifyGate.Task;
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 791000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.Classifying, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake));
        await Task.Delay(200);

        classifyGate.SetResult();

        // After gate release: ClassifyRequest.Completed fires, sees HasPendingEdit, loops back to ProjectMessage.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Enriched after HasPendingEdit re-loop from Classifying");

        projectCount.ShouldBe(2, "Two ProjectMessage requests: initial and re-loop after pending edit");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeFalse("HasPendingEdit cleared after re-loop");
    }

    // -------------------------------------------------------------------------
    // Test 20: MessageCreatedAt populated from snowflake
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageCreatedAt_populated_correctly_from_snowflake()
    {
        // Use a real Discord snowflake known to decode to a specific timestamp.
        // Snowflake 175928847299117063 is the Discord epoch baseline + small offset;
        // decoded: (175928847299117063 >> 22) + 1420070400000 = 1462015105796 ms Unix
        // = 2016-04-30T11:18:25.796Z.
        const long snowflake = 175928847299117063L;
        var expectedCreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            (snowflake >> 22) + 1420070400000L);

        var clock = MakeClock();
        await using var provider = BuildProvider(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<MessageCaptured>(new
        {
            MessageSnowflake = snowflake,
            ChannelId = 1000L,
            GuildId = 2000L,
            AuthorId = 3000L,
            AuthorIsBot = false,
            PayloadJson = """{"id":"175928847299117063","content":"timestamp test"}""",
            CurrentState = "Initial",
            UpdatedOn = FixedNow,
            HomeChannelName = (string?)null,
        });

        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.MessageCreatedAt.ShouldBe(expectedCreatedAt,
            "MessageCreatedAt must match the snowflake-decoded timestamp");
    }

    // =========================================================================
    // Re-enrichment fan-out tests (ClassificationInvalidated / TagsInvalidated)
    // =========================================================================

    // Helper: drives a saga all the way to Enriched with the given model versions stamped.
    private static ServiceProvider BuildProviderWithModels(
        ISystemClock clock,
        string embeddingModel,
        string tagModel)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = embeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = tagModel,
                        IndexedAt = FixedIndexedAt,
                    }));
            })
            .BuildServiceProvider(true);
    }

    // -------------------------------------------------------------------------
    // Test 21: ClassificationInvalidated with new model version re-enters Classifying
    //          then reaches Enriched with updated EmbeddingModelVersion.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassificationInvalidated_with_new_model_triggers_re_classify_and_stamps_version()
    {
        var clock = MakeClock();
        const string oldModel = "nomic-embed-text";
        const string newModel = "mxbai-embed-large";

        var classifyCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    Interlocked.Increment(ref classifyCount);
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 800000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        // Drive saga to Enriched with old model.
        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaBefore = sagaHarness.Sagas.Contains(expectedId);
        sagaBefore!.EmbeddingModelVersion.ShouldBe(oldModel);

        // Publish re-classify request — cutoff after UpdatedOn so this saga matches.
        // UpdatedOn now derives from ctx.SentTime (real clock), so use a reliably future cutoff.
        await harness.Bus.Publish<ClassificationInvalidated>(new
        {
            ModelVersion = newModel,
            Cutoff = FixedIndexedAt.AddHours(1),
        });

        // Saga should leave Enriched, enter Classifying (tag skipped), then return to Enriched.
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.Classifying, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        classifyCount.ShouldBe(2, "ClassifyRequest should run twice: initial + re-classify");
    }

    // -------------------------------------------------------------------------
    // Test 22: ClassificationInvalidated with same model version — saga NOT matched.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassificationInvalidated_same_model_version_does_not_match()
    {
        var clock = MakeClock();

        await using var provider = BuildProviderWithModels(clock, StubEmbeddingModel, StubTagModel);
        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 801000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        // Same classify model version — CorrelateBy predicate rejects this saga.
        await harness.Bus.Publish<ClassificationInvalidated>(new
        {
            ModelVersion = StubTagModel,
            Cutoff = FixedNow.AddHours(1),
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.CurrentState.ShouldBe("Enriched", "Saga must not be disturbed when classify model version matches");
    }

    // -------------------------------------------------------------------------
    // Test 23: ClassificationInvalidated with cutoff BEFORE saga.UpdatedOn — no match.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassificationInvalidated_cutoff_before_last_updated_at_does_not_match()
    {
        var clock = MakeClock();

        await using var provider = BuildProviderWithModels(clock, StubEmbeddingModel, StubTagModel);
        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 802000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        // Cutoff in the past (before FixedNow = UpdatedOn) — predicate rejects.
        await harness.Bus.Publish<ClassificationInvalidated>(new
        {
            ModelVersion = "mxbai-embed-large",
            Cutoff = FixedNow.AddHours(-1),
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.CurrentState.ShouldBe("Enriched", "Cutoff before UpdatedOn must exclude the saga");
    }

    // -------------------------------------------------------------------------
    // Test 24: ClassificationInvalidated while saga is not in Enriched — no match.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassificationInvalidated_saga_not_in_Enriched_is_not_matched()
    {
        var clock = MakeClock();

        // Provider that parks at AnalyzeMessage.Pending (no handler).
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();
                // No AnalyzeMessage handler — saga stays in AnalyzeMessage.Pending.
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 803000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, machine.AnalyzeMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<ClassificationInvalidated>(new
        {
            ModelVersion = "mxbai-embed-large",
            Cutoff = FixedNow.AddHours(1),
        });

        await Task.Delay(300);

        // Saga is in AnalyzeMessage_Pending — CorrelateBy requires CurrentState == "Enriched".
        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.CurrentState.ShouldNotBe("Enriched",
            "Saga in non-Enriched state must not be matched by ClassificationInvalidated");
    }

    // -------------------------------------------------------------------------
    // Test 25: TagsInvalidated with new embedding model re-enters Tagging and stamps EmbeddingModelVersion.
    // -------------------------------------------------------------------------

    [Test]
    public async Task TagsInvalidated_with_new_model_triggers_re_tag_classify_and_stamps_version()
    {
        var clock = MakeClock();
        const string oldEmbeddingModel = "nomic-embed-text";
        const string newEmbeddingModel = "mxbai-embed-large";

        var tagCount = 0;
        var classifyCount = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    var embeddingModel = Interlocked.Increment(ref tagCount) == 1
                        ? oldEmbeddingModel
                        : newEmbeddingModel;
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = embeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    Interlocked.Increment(ref classifyCount);
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 810000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaBefore = sagaHarness.Sagas.Contains(expectedId);
        sagaBefore!.EmbeddingModelVersion.ShouldBe(oldEmbeddingModel);

        // UpdatedOn now derives from ctx.SentTime (real clock), so use a reliably future cutoff.
        await harness.Bus.Publish<TagsInvalidated>(new
        {
            ModelVersion = newEmbeddingModel,
            Cutoff = FixedIndexedAt.AddHours(1),
        });

        // TagsInvalidated re-enters from Tagging (both Tag + Classify re-run).
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.Tagging, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaAfter = sagaHarness.Sagas.Contains(expectedId);
        sagaAfter!.EmbeddingModelVersion.ShouldBe(newEmbeddingModel,
            "EmbeddingModelVersion must be stamped from second TagRequest.Completed");
        tagCount.ShouldBe(2, "TagRequest should run twice: initial + re-tag");
        classifyCount.ShouldBe(2, "ClassifyRequest should run twice: initial + re-classify after re-tag");
    }

    // -------------------------------------------------------------------------
    // Test 26: TagsInvalidated with same model version — saga NOT matched.
    // -------------------------------------------------------------------------

    [Test]
    public async Task TagsInvalidated_same_model_version_does_not_match()
    {
        var clock = MakeClock();

        await using var provider = BuildProviderWithModels(clock, StubEmbeddingModel, StubTagModel);
        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 811000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        await harness.Bus.Publish<TagsInvalidated>(new
        {
            ModelVersion = StubEmbeddingModel,
            Cutoff = FixedNow.AddHours(1),
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.CurrentState.ShouldBe("Enriched", "Saga must not be disturbed when embedding model version matches");
    }

    // =========================================================================
    // Replay from Faulted — MessageReplayRequested
    // =========================================================================

    // Helper: drives a saga to Faulted on the Tag phase (no embedding stored).
    private static ServiceProvider BuildProviderTagFaulted(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>((Func<ConsumeContext<TagMessageRequested>, Task>)(_ =>
                    throw new InvalidOperationException("embedding unavailable")));
            })
            .BuildServiceProvider(true);
    }

    // Helper: drives a saga to Faulted on the Classify phase (embedding stored, tags missing).
    private static ServiceProvider BuildProviderClassifyFaulted(ISystemClock clock)
    {
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>((Func<ConsumeContext<ClassifyMessageRequested>, Task>)(_ =>
                    throw new InvalidOperationException("llm unavailable")));
            })
            .BuildServiceProvider(true);
    }

    // -------------------------------------------------------------------------
    // Test 27: MessageReplayRequested with phase=null re-enters Tagging from TagFaulted.
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageReplayRequested_phase_null_re_enters_Tagging_from_TagFaulted_saga()
    {
        var clock = MakeClock();

        var tagCount = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    // First call faults (consumer throws → Fault<TagMessageRequested>), second succeeds (replay).
                    if (Interlocked.Increment(ref tagCount) == 1)
                        throw new InvalidOperationException("first attempt fails");

                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    }));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 820000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));

        var faultedSaga = sagaHarness.Sagas.Contains(expectedId);
        faultedSaga.ShouldNotBeNull();
        faultedSaga.Embedding.ShouldBeNull("No embedding stored when Tag faulted");

        // Publish replay — phase null matches all Faulted sagas.
        await harness.Bus.Publish<MessageReplayRequested>(new
        {
            Timestamp = FixedNow,
            Phase = (string?)null,
        });

        // Saga re-enters Tagging, completes Tag + Classify, reaches Enriched.
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var enrichedSaga = sagaHarness.Sagas.Contains(expectedId);
        enrichedSaga.ShouldNotBeNull();
        enrichedSaga.Embedding.ShouldNotBeNull("Embedding must be set after successful replay");
        enrichedSaga.Tags.ShouldNotBeNull("Tags must be set after successful replay");
        tagCount.ShouldBe(2, "TagRequest must run twice: initial fault + replay");
    }

    // -------------------------------------------------------------------------
    // Test 28: MessageReplayRequested with phase=classify re-enters Classifying
    //          from ClassifyFaulted saga (embedding preserved from first Tag run).
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageReplayRequested_phase_classify_re_enters_Classifying_from_ClassifyFaulted_saga()
    {
        var clock = MakeClock();

        var classifyCount = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = StubEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    // First call faults (consumer throws → Fault<ClassifyMessageRequested>),
                    // second succeeds (replay skips Tag — embedding preserved).
                    if (Interlocked.Increment(ref classifyCount) == 1)
                        throw new InvalidOperationException("first classify attempt fails");

                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 821000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));

        var faultedSaga = sagaHarness.Sagas.Contains(expectedId);
        faultedSaga.ShouldNotBeNull();
        faultedSaga.Embedding.ShouldNotBeNull("Embedding preserved after Classify fault");
        faultedSaga.Tags.ShouldBeNull("Tags not stored when Classify faulted");

        // Publish replay with phase=classify — only ClassifyFaulted sagas match.
        await harness.Bus.Publish<MessageReplayRequested>(new
        {
            Timestamp = FixedNow,
            Phase = "classify",
        });

        // Saga re-enters Classifying directly (Tag skipped), reaches Enriched.
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.Classifying, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var enrichedSaga = sagaHarness.Sagas.Contains(expectedId);
        enrichedSaga.ShouldNotBeNull();
        enrichedSaga.Tags.ShouldNotBeNull("Tags set after successful classify replay");
        // TagRequest ran exactly once — replay does not re-embed.
        classifyCount.ShouldBe(2, "ClassifyRequest must run twice: initial fault + replay");
    }

    // -------------------------------------------------------------------------
    // Test 29: TagFault — embedding not stored, saga settles in Faulted.
    //          (ClearRequestIdOnFaulted is no longer applicable — Tag/Classify are
    //          event-driven; no request IDs on the saga for these phases.)
    // -------------------------------------------------------------------------

    [Test]
    public async Task TagFault_saga_has_no_embedding_in_Faulted_state()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderTagFaulted(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 822000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.Embedding.ShouldBeNull("Embedding must not be stored when TagMessageRequested consumer faults");
    }

    // -------------------------------------------------------------------------
    // Test 30: MessageReplayRequested phase=tag does not match a ClassifyFaulted saga.
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageReplayRequested_phase_tag_does_not_match_ClassifyFaulted_saga()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderClassifyFaulted(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 823000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));

        // Verify this is a ClassifyFaulted saga (embedding present, tags null).
        var faultedSaga = sagaHarness.Sagas.Contains(expectedId);
        faultedSaga.ShouldNotBeNull();
        faultedSaga.Embedding.ShouldNotBeNull();
        faultedSaga.Tags.ShouldBeNull();

        // phase=tag only matches sagas with Embedding == null — this saga has embedding, so no match.
        await harness.Bus.Publish<MessageReplayRequested>(new
        {
            Timestamp = FixedNow,
            Phase = "tag",
        });

        await Task.Delay(300);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga!.CurrentState.ShouldBe("Faulted",
            "phase=tag must not match a ClassifyFaulted saga (which has embedding stored)");
    }

    // =========================================================================
    // Gap coverage tests added post-review
    // =========================================================================

    // -------------------------------------------------------------------------
    // Test 31: MessageReplayRequested with no Faulted sagas → OnMissingInstance.Discard
    //          No exception, no phantom saga, no state transition.
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageReplayRequested_with_no_Faulted_sagas_discards_safely()
    {
        var clock = MakeClock();

        // Harness with a saga parked in Enriched (not Faulted) — predicate will not match.
        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 830000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        // Publish replay against a harness where no saga is in Faulted state.
        // OnMissingInstance.Discard must swallow this without faulting the harness.
        await harness.Bus.Publish<MessageReplayRequested>(new
        {
            Timestamp = FixedNow,
            Phase = (string?)null,
        });

        await Task.Delay(300);

        // Original saga must remain in Enriched — no unexpected transition.
        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.CurrentState.ShouldBe("Enriched",
            "Enriched saga must not be disturbed when MessageReplayRequested finds no Faulted match");

        // No phantom saga created for the replay event (repo still has exactly one saga).
        sagaHarness.Sagas.Count().ShouldBe(1,
            "OnMissingInstance.Discard must not create a phantom saga instance");
    }

    // -------------------------------------------------------------------------
    // Test 32: MessageReplayRequested with no sagas at all → Discard, no exception.
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageReplayRequested_with_empty_repository_discards_safely()
    {
        var clock = MakeClock();

        // Empty harness — no sagas published at all.
        await using var provider = BuildProvider(clock);
        var harness = provider.GetTestHarness();
        await harness.Start();

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        // Should not throw — OnMissingInstance.Discard handles zero matches.
        await harness.Bus.Publish<MessageReplayRequested>(new
        {
            Timestamp = FixedNow,
            Phase = "tag",
        });

        await Task.Delay(300);

        sagaHarness.Sagas.Count().ShouldBe(0,
            "No sagas should be created when MessageReplayRequested finds no matches");
    }

    // -------------------------------------------------------------------------
    // Test 33: TagsInvalidated end-to-end — re-enters Tagging, stamps EmbeddingModelVersion
    //          to the new model after second TagRequest.Completed.
    // -------------------------------------------------------------------------

    [Test]
    public async Task TagsInvalidated_stamps_EmbeddingModelVersion_to_new_model_after_re_tag()
    {
        var clock = MakeClock();
        const string oldEmbeddingModel = "nomic-embed-text";
        const string newEmbeddingModel = "mxbai-embed-large";

        var tagCount = 0;
        var classifyCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                {
                    // First call returns old model; second (re-tag) returns new model.
                    var embeddingModel = Interlocked.Increment(ref tagCount) == 1
                        ? oldEmbeddingModel
                        : newEmbeddingModel;
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = embeddingModel,
                    });
                });

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    Interlocked.Increment(ref classifyCount);
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = StubTagModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 840000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        // Drive to Enriched with old embedding model.
        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaBefore = sagaHarness.Sagas.Contains(expectedId);
        sagaBefore!.EmbeddingModelVersion.ShouldBe(oldEmbeddingModel,
            "EmbeddingModelVersion must be stamped from first TagRequest");

        // TagsInvalidated predicate: EmbeddingModelVersion != ModelVersion.
        // Drive re-tag by requesting an EmbeddingModelVersion the saga does not currently have.
        // UpdatedOn now derives from ctx.SentTime (real clock), so use a reliably future cutoff.
        const string newTagModel = "llama3.1:70b";
        await harness.Bus.Publish<TagsInvalidated>(new
        {
            ModelVersion = newTagModel,
            Cutoff = FixedIndexedAt.AddHours(1),
        });

        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.Tagging, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaAfter = sagaHarness.Sagas.Contains(expectedId);
        sagaAfter.ShouldNotBeNull();
        sagaAfter.EmbeddingModelVersion.ShouldBe(newEmbeddingModel,
            "EmbeddingModelVersion must be re-stamped from second Tagged event under new model");
        tagCount.ShouldBe(2, "TagMessageRequested fired exactly twice: initial + re-tag");
        classifyCount.ShouldBe(2, "ClassifyMessageRequested fired exactly twice: initial + re-classify after re-tag");
    }

    // -------------------------------------------------------------------------
    // Test 34: ClassificationInvalidated end-to-end — re-enters Classifying, stamps ClassifyModelVersion
    //          to the new model after second ClassifyRequest.Completed.
    // -------------------------------------------------------------------------

    [Test]
    public async Task ClassificationInvalidated_end_to_end_stamps_ClassifyModelVersion_to_new_model()
    {
        var clock = MakeClock();
        const string oldEmbeddingModel = "nomic-embed-text";
        const string newTagModel = "llama3.1:70b";

        var classifyCount = 0;

        await using var provider = new ServiceCollection()
            .AddSingleton(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
                    .InMemoryRepository();

                cfg.AddHandler<AnalyzeMessageRequest>(async ctx =>
                    await ctx.RespondAsync<AnalyzeMessageResponse>(new
                    {
                        IsSubstantive = true, IsBot = false, DetectedLanguage = "en",
                    }));

                cfg.AddHandler<ProjectMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new ProjectMessageResponse(StubIR)));

                cfg.AddHandler<TagMessageRequested>(async ctx =>
                    await ctx.Publish<MessageTagged>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Embedding = StubEmbedding,
                        EmbeddingModelVersion = oldEmbeddingModel,
                    }));

                cfg.AddHandler<ClassifyMessageRequested>(async ctx =>
                {
                    // Second classify call (re-classify) returns the new classify model version.
                    var classifyModel = Interlocked.Increment(ref classifyCount) == 1
                        ? StubTagModel
                        : newTagModel;
                    await ctx.Publish<MessageClassified>(new
                    {
                        ctx.Message.MessageSnowflake,
                        Tags = StubTags,
                        ClassifyModelVersion = classifyModel,
                        IndexedAt = FixedIndexedAt,
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 841000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);
        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();

        // Drive to Enriched with old embedding model.
        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaBefore = sagaHarness.Sagas.Contains(expectedId);
        sagaBefore!.EmbeddingModelVersion.ShouldBe(oldEmbeddingModel,
            "Initial EmbeddingModelVersion must match TagRequest response");
        sagaBefore.ClassifyModelVersion.ShouldBe(StubTagModel,
            "Initial ClassifyModelVersion must match first ClassifyRequest response");

        // Publish ClassificationInvalidated with a new classify model — predicate: ClassifyModelVersion != ModelVersion.
        // UpdatedOn now derives from ctx.SentTime (real clock), so use a reliably future cutoff.
        await harness.Bus.Publish<ClassificationInvalidated>(new
        {
            ModelVersion = newTagModel,
            Cutoff = FixedIndexedAt.AddHours(1),
        });

        // Saga must leave Enriched, enter Classifying (Tag skipped), then return to Enriched.
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();
        await sagaHarness.Exists(expectedId, machine.Classifying, timeout: TimeSpan.FromSeconds(5));
        await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));

        var sagaAfter = sagaHarness.Sagas.Contains(expectedId);
        sagaAfter.ShouldNotBeNull();
        sagaAfter.ClassifyModelVersion.ShouldBe(newTagModel,
            "ClassifyModelVersion must be re-stamped from second ClassifyRequest.Completed");
        sagaAfter.EmbeddingModelVersion.ShouldBe(oldEmbeddingModel,
            "EmbeddingModelVersion must not change — ClassificationInvalidated skips TagRequest");
        classifyCount.ShouldBe(2, "ClassifyRequest fired exactly twice: initial + re-classify");
    }
}
