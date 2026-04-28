using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Contracts;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Sagas;

[TestFixture]
public sealed class MessageSagaStateMachineTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedIndexedAt = new(2026, 1, 15, 12, 5, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<string> StubTags = ["dotnet", "csharp"];
    private static readonly IReadOnlyList<float> StubEmbedding = [0.1f, 0.2f, 0.3f];

    private static readonly MessageIR StubIR = new(
        Body: new[] { new TextNode("hello") },
        Attachments: Array.Empty<AttachmentIR>(),
        Embeds: Array.Empty<EmbedIR>(),
        ReplyTo: null,
        CapturedAt: FixedNow);

    private static ISystemClock MakeClock() =>
        Substitute.For<ISystemClock>().With(c => c.UtcNow.Returns(FixedNow));

    // Builds an in-memory harness wired through the full happy path:
    // Analyze → Project → Enhance → Index → Enriched.
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding));
                });

                cfg.AddHandler<IndexMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<IndexMessageResponse>(new { IndexedAt = FixedIndexedAt });
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

    // Provider that stubs through Project but parks at EnhanceMessage.Pending.
    private static ServiceProvider BuildProviderEnhancePending(ISystemClock clock)
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
                // No EnhanceMessage handler — saga stays in EnhanceMessage.Pending
            })
            .BuildServiceProvider(true);
    }

    // Provider that stubs through Enhance but parks at IndexMessage.Pending.
    private static ServiceProvider BuildProviderIndexPending(ISystemClock clock)
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding));
                });
                // No IndexMessage handler — saga stays in IndexMessage.Pending
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
        LastUpdatedAt = FixedNow,
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
        LastUpdatedAt = FixedNow,
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
            LastUpdatedAt = FixedNow,
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
            LastUpdatedAt = FixedNow,
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

                // Enhance + Index stubs so saga can complete after the re-loop.
                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding));
                });

                cfg.AddHandler<IndexMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<IndexMessageResponse>(new { IndexedAt = FixedIndexedAt });
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding));
                });

                cfg.AddHandler<IndexMessageRequest>(async ctx =>
                {
                    await ctx.RespondAsync<IndexMessageResponse>(new { IndexedAt = FixedIndexedAt });
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
            LastUpdatedAt = FixedNow,
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
    // Edit re-loop tests during EnhanceMessage.Pending and IndexMessage.Pending
    // =========================================================================

    // -------------------------------------------------------------------------
    // Test 12: EnhanceMessage.Faulted → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task EnhanceMessage_Faulted_transitions_to_Faulted()
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

                cfg.AddHandler<EnhanceMessageRequest>((Func<ConsumeContext<EnhanceMessageRequest>, Task>)(_ =>
                    throw new InvalidOperationException("ollama exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 760000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when EnhanceMessage consumer throws");
    }

    // -------------------------------------------------------------------------
    // Test 13: EnhanceMessage.TimeoutExpired → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task EnhanceMessage_TimeoutExpired_transitions_to_Faulted()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderEnhancePending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 761000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.EnhanceMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        var requestId = saga.EnhanceMessageRequestId;
        requestId.ShouldNotBeNull("EnhanceMessageRequestId must be set while in Pending");

        await harness.Bus.Publish<RequestTimeoutExpired<EnhanceMessageRequest>>(new
        {
            RequestId = requestId.Value,
            CorrelationId = expectedId,
            Timestamp = DateTime.UtcNow,
            ExpirationTime = DateTime.UtcNow,
        });

        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(5));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when EnhanceMessage timeout fires");
    }

    // -------------------------------------------------------------------------
    // Test 14: IndexMessage.Faulted → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task IndexMessage_Faulted_transitions_to_Faulted()
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding)));

                cfg.AddHandler<IndexMessageRequest>((Func<ConsumeContext<IndexMessageRequest>, Task>)(_ =>
                    throw new InvalidOperationException("pgvector exploded")));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 770000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when IndexMessage consumer throws");
    }

    // -------------------------------------------------------------------------
    // Test 15: IndexMessage.TimeoutExpired → Faulted
    // -------------------------------------------------------------------------

    [Test]
    public async Task IndexMessage_TimeoutExpired_transitions_to_Faulted()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderIndexPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 771000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.IndexMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        var requestId = saga.IndexMessageRequestId;
        requestId.ShouldNotBeNull("IndexMessageRequestId must be set while in Pending");

        await harness.Bus.Publish<RequestTimeoutExpired<IndexMessageRequest>>(new
        {
            RequestId = requestId.Value,
            CorrelationId = expectedId,
            Timestamp = DateTime.UtcNow,
            ExpirationTime = DateTime.UtcNow,
        });

        var sagaId = await sagaHarness.Exists(expectedId, m => m.Faulted, timeout: TimeSpan.FromSeconds(5));
        sagaId.ShouldNotBeNull("Saga should reach Faulted when IndexMessage timeout fires");
    }

    // -------------------------------------------------------------------------
    // Test 16: EditObserved during EnhanceMessage.Pending → HasPendingEdit=true
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageEditObserved_during_EnhancePending_sets_HasPendingEdit()
    {
        var clock = MakeClock();
        await using var provider = BuildProviderEnhancePending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 780000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.EnhanceMessage.Pending, timeout: TimeSpan.FromSeconds(5));

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
        await using var provider = BuildProviderIndexPending(clock);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 781000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.IndexMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        var editedAt = FixedNow.AddHours(2);
        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake, editedAt));
        await Task.Delay(200);

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeTrue("HasPendingEdit should be set when edit arrives during IndexPending");
        saga.EditedTimestamp.ShouldBe(editedAt);
    }

    // -------------------------------------------------------------------------
    // Test 18: HasPendingEdit re-loop after EnhanceMessage.Completed → re-Request(ProjectMessage)
    // -------------------------------------------------------------------------

    [Test]
    public async Task HasPendingEdit_during_EnhancePending_triggers_re_ProjectMessage()
    {
        var clock = MakeClock();

        var enhanceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                {
                    // Block until the test has published an edit — ensures HasPendingEdit is
                    // set before the Completed handler fires.
                    await enhanceGate.Task;
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding));
                });

                cfg.AddHandler<IndexMessageRequest>(async ctx =>
                    await ctx.RespondAsync<IndexMessageResponse>(new { IndexedAt = FixedIndexedAt }));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        const long snowflake = 790000000000000001L;
        var expectedId = DeterministicGuid.FromSnowflake(snowflake);

        await harness.Bus.Publish<MessageCaptured>(BuildMessageCaptured(snowflake));

        var sagaHarness = harness.GetSagaStateMachineHarness<MessageSagaStateMachine, MessageSagaState>();
        var machine = provider.GetRequiredService<MessageSagaStateMachine>();

        await sagaHarness.Exists(expectedId, machine.EnhanceMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake));
        await Task.Delay(200);

        enhanceGate.SetResult();

        // After gate release: EnhanceMessage.Completed fires, sees HasPendingEdit, loops back to
        // ProjectMessage → Enhance → Index → Enriched.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Enriched after HasPendingEdit re-loop from Enhance");

        // projectCount == 2: initial + re-loop
        projectCount.ShouldBe(2, "Two ProjectMessage requests: initial and re-loop after pending edit");

        var saga = sagaHarness.Sagas.Contains(expectedId);
        saga.ShouldNotBeNull();
        saga.HasPendingEdit.ShouldBeFalse("HasPendingEdit cleared after re-loop");
    }

    // -------------------------------------------------------------------------
    // Test 19: HasPendingEdit re-loop after IndexMessage.Completed → re-Request(ProjectMessage)
    // -------------------------------------------------------------------------

    [Test]
    public async Task HasPendingEdit_during_IndexPending_triggers_re_ProjectMessage()
    {
        var clock = MakeClock();

        var indexGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

                cfg.AddHandler<EnhanceMessageRequest>(async ctx =>
                    await ctx.RespondAsync(new EnhanceMessageResponse(
                        Tags: StubTags,
                        Embedding: StubEmbedding)));

                cfg.AddHandler<IndexMessageRequest>(async ctx =>
                {
                    await indexGate.Task;
                    await ctx.RespondAsync<IndexMessageResponse>(new { IndexedAt = FixedIndexedAt });
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

        await sagaHarness.Exists(expectedId, machine.IndexMessage.Pending, timeout: TimeSpan.FromSeconds(5));

        await harness.Bus.Publish<MessageEditObserved>(BuildMessageEditObserved(snowflake));
        await Task.Delay(200);

        indexGate.SetResult();

        // After gate release: IndexMessage.Completed fires, sees HasPendingEdit, loops back to
        // ProjectMessage → Enhance → Index → Enriched.
        var sagaId = await sagaHarness.Exists(expectedId, m => m.Enriched, timeout: TimeSpan.FromSeconds(10));
        sagaId.ShouldNotBeNull("Saga should reach Enriched after HasPendingEdit re-loop from Index");

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
            LastUpdatedAt = FixedNow,
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
}
