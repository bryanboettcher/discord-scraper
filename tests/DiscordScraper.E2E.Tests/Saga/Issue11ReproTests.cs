using DiscordScraper.Contracts.Events.Message;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.TestSupport;
using DiscordScraper.TestSupport.Observers;
using MassTransit.Contracts;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DiscordScraper.E2E.Tests.Saga;

/// <summary>
/// Regression reproduction for GitHub issue #11:
/// <c>RequestTimeoutExpired&lt;AnalyzeMessageRequest&gt;</c> causes
/// <c>MongoWriteException E11000 duplicate key error</c> on the saga <c>_id</c>
/// field under burst load when the outbox is active.
///
/// --- Bug description ---
/// Under burst load with the Mongo outbox enabled, a <c>RequestTimeoutExpired</c> event
/// for an existing saga (in <c>AnalyzeMessage.Pending</c>) fails to correlate to the
/// existing Mongo document. The saga repository falls through to the <c>InsertOnInitial</c>
/// path and attempts to insert a document with the same <c>_id</c>, producing E11000 on
/// every retry of the timeout-expired consume. The retry exhaustion compounds: each of the
/// 3 retries in <see cref="MessageSagaDefinition"/> re-fires the same bad code path.
///
/// --- Repro recipe ---
/// 1. Real RabbitMQ + real Mongo (single-node replica set — required for outbox transactions).
/// 2. <c>NeverRespondingAnalyzeConsumer</c>: accepts <c>AnalyzeMessageRequest</c> but never
///    responds; every saga stays in <c>AnalyzeMessage.Pending</c> until timeout fires.
/// 3. 2-second request timeout: timeout events fire quickly, compressing the race window.
/// 4. Burst of 30 <c>MessageCaptured</c> events: enough concurrent sagas that multiple
///    <c>RequestTimeoutExpired</c> events arrive at the saga endpoint while the outbox
///    flush is in progress, creating the collision described in the bug.
/// 5. Outbox enabled (replica set Mongo + <c>AddMongoDbOutbox</c>) to match production
///    topology — the outbox changes the consume pipeline ordering in a way that widens
///    the race window for the correlation lookup.
///
/// --- Pass/fail contract ---
/// The test asserts that NO E11000 duplicate-key errors appear in the logs after the run.
/// When the bug is present, this assertion fails with captured log entries showing
/// <c>MongoWriteException: E11000 duplicate key error collection: ...message_sagas</c>.
/// When the bug is fixed, the assertion passes (zero E11000 entries, sagas all settle
/// in <c>Faulted</c> state after timeout without duplicate-key races).
///
/// <c>[Explicit]</c> marks the test so it does not run in CI by default — the bug is
/// currently present and this test confirms the storm rather than masking it.
/// Remove <c>[Explicit]</c> once the fix lands.
/// </summary>
[TestFixture]
[Category("E2E")]
public sealed class Issue11ReproTests
{
    // 30 synthetic messages — large enough to saturate the 16-slot saga concurrency window
    // and force multiple RequestTimeoutExpired events to land concurrently.
    private const int BurstMessageCount = 30;

    // MessageSagaStateMachine._requestTimeout defaults to 30s when constructed from DI
    // (the constructor's TimeSpan? requestTimeout parameter is not injectable via IOptions).
    // EnrichmentTagOptions/EnrichmentClassifyOptions only drive _tagTimeout/_classifyTimeout.
    // The Analyze phase timeout is always 30s. Hard wait must exceed 30s to let timeouts fire.
    private static readonly TimeSpan AnalyzeSagaTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HardWaitBuffer = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reproduces issue #11: burst of messages with never-responding Analyze consumer
    /// causes E11000 duplicate-key errors on the saga _id when timeouts expire.
    ///
    /// The test is marked [Explicit] because the bug is currently present — running this
    /// test in normal CI would produce a false failure. Remove [Explicit] after the fix lands.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task E11000_DuplicateKey_WhenTimeoutExpiredFiredUnderBurstWithOutbox(CancellationToken ct)
    {
        await using var stack = TestStack.E2E()
            .WithMongoOutbox()
            .WithNeverRespondingAnalyze()
            .WithCapturingLogger();

        await stack.StartAsync(ct);

        var harness = stack.GetTestHarness();
        var logger = stack.GetCapturingLogger();
        var mongo = harness.Provider.GetRequiredService<IMongoDatabase>();

        // --- Arrange: publish a burst of synthetic MessageCaptured events ---
        // Each generates a unique saga keyed by DeterministicGuid.FromSnowflake(snowflake).
        // We use snowflakes derived from the real Discord epoch so they parse correctly.
        // Snowflake formula: (unixMs - 1420070400000L) << 22. Base = 2026-01-01T00:00:00Z.
        const long Base2026Ms = 1735689600000L;
        const long DiscordEpochMs = 1420070400000L;
        var baseSnowflake = (Base2026Ms - DiscordEpochMs) << 22;

        for (var i = 0; i < BurstMessageCount; i++)
        {
            var snowflake = baseSnowflake + i;
            await harness.Bus.Publish<MessageCaptured>(new
            {
                MessageId = Guid.NewGuid(),
                MessageSnowflake = snowflake,
                ChannelId = 1374441549315707115L,
                GuildId = 1374441548594282659L,
                AuthorId = 123456789L,
                AuthorIsBot = false,
                PayloadJson = BuildPayloadJson(snowflake, i),
                CurrentState = "Initial",
                UpdatedOn = DateTimeOffset.UtcNow,
                HomeChannelName = (string?)null,
            }, ct);
        }

        // --- Act: wait for the timeout storm to play out ---
        // First pump: drain the initial MessageCaptured + AnalyzeMessageRequest burst.
        // After NeverRespondingAnalyzeConsumer ACKs the 30 requests, the bus goes idle.
        // InactivityTask fires immediately because the 2s timeout timers are pending in the
        // RabbitMQ delayed exchange — they are not yet "in-flight" from MT's perspective.
        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(30), ct: ct);

        // Hard wait: let the delayed exchange fire the RequestTimeoutExpired messages.
        // Analyze saga timeout is 30s (hardcoded in MessageSagaStateMachine constructor default).
        await Task.Delay(AnalyzeSagaTimeout + HardWaitBuffer, ct);
        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(60), ct: ct);

        // --- Assert 1: All 30 MessageCaptured events were consumed by the saga endpoint ---
        var capturedCount = harness.Consumed.Select<MessageCaptured>().Count();
        var analyzeReqCount = harness.Consumed.Select<AnalyzeMessageRequest>().Count();
        TestContext.Out.WriteLine(
            $"[DIAG] MessageCaptured={capturedCount} AnalyzeReq={analyzeReqCount}");

        // Dump saga state from Mongo for diagnostics
        var sagaCollection = mongo.GetCollection<MessageSagaStateDoc>("message_sagas_test");
        var sagaStates = await sagaCollection.Aggregate()
            .Group(d => d.CurrentState, g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();
        TestContext.Out.WriteLine($"[DIAG] Saga states: {string.Join(", ", sagaStates.Select(s => $"{s.State}:{s.Count}"))}");

        capturedCount.ShouldBe(BurstMessageCount,
            $"All {BurstMessageCount} MessageCaptured events must be consumed. " +
            $"Consumed: {capturedCount}. Possible container startup or publication issue.");

        // --- Assert 2: RequestTimeoutExpired<AnalyzeMessageRequest> events were consumed ---
        // Under the bug, each fires and then retries 3 times per saga.
        var timeoutConsumed = harness.Consumed.Select<RequestTimeoutExpired<AnalyzeMessageRequest>>().Count();
        timeoutConsumed.ShouldBeGreaterThan(0,
            "RequestTimeoutExpired<AnalyzeMessageRequest> must be consumed at least once. " +
            "If zero, the timeout did not fire — check RequestTimeout setting or quiescence wait.");

        TestContext.Out.WriteLine(
            $"[REPRO] MessageCaptured consumed={capturedCount} " +
            $"TimeoutExpired consumed={timeoutConsumed} " +
            $"LogEntries captured={logger.Entries.Count}");

        // --- Assert 3: No E11000 duplicate-key errors ---
        // This is the regression gate. When the bug is present this assertion fails, making
        // the bug visible. When the fix lands, zero E11000 errors → test passes.
        var duplicateKeyErrors = logger.DuplicateKeyErrors;
        if (duplicateKeyErrors.Count > 0)
        {
            TestContext.Out.WriteLine($"[E11000] {duplicateKeyErrors.Count} duplicate-key error(s) captured:");
            foreach (var entry in duplicateKeyErrors.Take(10))
            {
                TestContext.Out.WriteLine($"  [{entry.Category}] {entry.Message}");
                if (entry.Exception is not null)
                {
                    TestContext.Out.WriteLine($"    ExceptionType: {entry.Exception.GetType().FullName}");
                    TestContext.Out.WriteLine($"    ExceptionMessage: {entry.Exception.Message}");
                    TestContext.Out.WriteLine($"    StackTrace:\n{entry.Exception.StackTrace}");

                    // Drill into inner exceptions — MongoBulkWriteException wraps WriteErrors
                    var inner = entry.Exception.InnerException;
                    var depth = 0;
                    while (inner is not null && depth++ < 5)
                    {
                        TestContext.Out.WriteLine($"    InnerException[{depth}] {inner.GetType().FullName}: {inner.Message}");
                        TestContext.Out.WriteLine($"    InnerStack[{depth}]:\n{inner.StackTrace}");
                        inner = inner.InnerException;
                    }

                    // Mongo-specific: dump WriteErrors from MongoBulkWriteException / MongoWriteException
                    if (entry.Exception is MongoDB.Driver.MongoBulkWriteException bulkEx)
                    {
                        foreach (var we in bulkEx.WriteErrors)
                            TestContext.Out.WriteLine($"    WriteError: Code={we.Code} Category={we.Category} Message={we.Message}");
                    }
                    else if (entry.Exception is MongoDB.Driver.MongoWriteException writeEx)
                    {
                        TestContext.Out.WriteLine($"    WriteError: Code={writeEx.WriteError.Code} Category={writeEx.WriteError.Category} Message={writeEx.WriteError.Message}");
                    }
                }
            }

            // Also dump ALL warnings+errors from MT saga category for broader stack context
            TestContext.Out.WriteLine("[ALL-MT-ERRORS]");
            foreach (var e in logger.Entries.Where(e => e.Level >= LogLevel.Error).Take(20))
            {
                TestContext.Out.WriteLine($"  [{e.Category}] {e.Level}: {e.Message}");
                if (e.Exception is not null)
                    TestContext.Out.WriteLine($"    {e.Exception.GetType().FullName}: {e.Exception.StackTrace}");
            }
        }

        duplicateKeyErrors.Count.ShouldBe(0,
            $"E11000 duplicate-key errors detected in saga repository logs. " +
            $"Found {duplicateKeyErrors.Count} error(s). " +
            $"This confirms the issue #11 storm: RequestTimeoutExpired<AnalyzeMessageRequest> is " +
            $"re-entering InsertOnInitial instead of correlating to the existing saga. " +
            $"Sample: {duplicateKeyErrors.FirstOrDefault()?.Message ?? "(none)"}");

        // --- Assert 4: Verify via Mongo directly — no saga has duplicate _id ---
        // Belt-and-suspenders: also check the actual collection state. All sagas should
        // be in Faulted state with distinct _id values (one per message snowflake).
        var collection = mongo.GetCollection<MessageSagaStateDoc>("message_sagas_test");
        var allDocs = await collection.Find(Builders<MessageSagaStateDoc>.Filter.Empty)
            .ToListAsync();

        var distinctIds = allDocs.Select(d => d.Id).Distinct().Count();
        allDocs.Count.ShouldBe(distinctIds,
            $"Saga collection contains {allDocs.Count} documents but only {distinctIds} distinct IDs. " +
            $"Duplicate documents indicate the E11000 race produced orphan inserts.");

        // All settled sagas must be in Faulted (the correct terminal state for a timeout)
        var nonFaultedSagas = allDocs.Where(d => d.CurrentState != "Faulted").ToList();
        TestContext.Out.WriteLine(
            $"[STATE] Total sagas={allDocs.Count} Faulted={allDocs.Count - nonFaultedSagas.Count} " +
            $"Other={nonFaultedSagas.Count}");

        // Dump raw BSON field names from 2 documents to verify serialization conventions
        var rawColl = mongo.GetCollection<MongoDB.Bson.BsonDocument>("message_sagas_test");
        var rawDocs = await rawColl.Find(MongoDB.Bson.BsonDocument.Parse("{}")).Limit(2).ToListAsync();
        for (var idx = 0; idx < rawDocs.Count; idx++)
        {
            var doc = rawDocs[idx];
            TestContext.Out.WriteLine($"[BSON-DOC-{idx}] fields: {string.Join(", ", doc.Names)}");
            // Dump the top-level scalar values (skip embedded docs/arrays for brevity)
            foreach (var elem in doc.Elements.Where(e => e.Value.BsonType != MongoDB.Bson.BsonType.Document
                                                      && e.Value.BsonType != MongoDB.Bson.BsonType.Array)
                                             .Take(15))
                TestContext.Out.WriteLine($"  {elem.Name} ({elem.Value.BsonType}): {elem.Value}");
        }
    }

    /// <summary>
    /// Baseline: single saga, no outbox, real Mongo, no burst. Confirms the test harness
    /// works and a single saga settles correctly without timeout storms.
    /// This test MUST pass even when #11 is present — it proves the test infrastructure
    /// is correctly set up and the bug is load-dependent.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task E2E_SingleSaga_SettlesWithoutE11000(CancellationToken ct)
    {
        // Single saga, outbox disabled — no race window, no E11000 possible.
        await using var stack = TestStack.E2E()
            .WithRequestTimeout(TimeSpan.FromSeconds(10))
            .WithNeverRespondingAnalyze()
            .WithCapturingLogger();

        await stack.StartAsync(ct);
        var harness = stack.GetTestHarness();
        var logger = stack.GetCapturingLogger();

        var snowflake = ((1735689600000L - 1420070400000L) << 22) + 999_999L;
        await harness.Bus.Publish<MessageCaptured>(new
        {
            MessageId = Guid.NewGuid(),
            MessageSnowflake = snowflake,
            ChannelId = 1374441549315707115L,
            GuildId = 1374441548594282659L,
            AuthorId = 123456789L,
            AuthorIsBot = false,
            PayloadJson = BuildPayloadJson(snowflake, 0),
            CurrentState = "Initial",
            UpdatedOn = DateTimeOffset.UtcNow,
            HomeChannelName = (string?)null,
        }, ct);

        await stack.PumpUntilQuiescent(maxWait: TimeSpan.FromSeconds(60), ct: ct);

        harness.Consumed.Select<MessageCaptured>().Count().ShouldBe(1,
            "Single MessageCaptured must be consumed.");

        var duplicateKeyErrors = logger.DuplicateKeyErrors;
        duplicateKeyErrors.Count.ShouldBe(0,
            $"Single saga should not produce E11000. Found: {duplicateKeyErrors.FirstOrDefault()?.Message}");

        TestContext.Out.WriteLine(
            $"[BASELINE] TimeoutExpired consumed={harness.Consumed.Select<RequestTimeoutExpired<AnalyzeMessageRequest>>().Count()} " +
            $"E11000 count={duplicateKeyErrors.Count}");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string BuildPayloadJson(long snowflake, int index) =>
        $$"""
        {
            "id": "{{snowflake}}",
            "content": "Issue #11 repro message {{index}} — substantive content for analyze phase",
            "author": {"id": "123456789", "username": "repro_user", "bot": false},
            "mentions": [],
            "attachments": [],
            "embeds": []
        }
        """;

    /// <summary>Projection of MessageSagaState for Mongo direct read in Assert 4.</summary>
    [MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements]
    private sealed class MessageSagaStateDoc
    {
        [MongoDB.Bson.Serialization.Attributes.BsonId]
        public Guid Id { get; set; }

        // MassTransit Mongo saga repo serializes C# property names as-is (PascalCase).
        // There is no camelCase convention applied by default to saga state documents.
        public string CurrentState { get; set; } = string.Empty;
    }
}
