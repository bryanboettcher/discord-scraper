using System.Collections;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Consumers;

// NSubstitute/Castle.Core can only create proxies for generic interfaces when all type
// arguments are publicly accessible. Because ConsumeContext<T> comes from strong-named
// MassTransit.Abstractions, T must be visible to DynamicProxyGenAssembly2. Declaring the
// test event/entity types at namespace scope (internal) satisfies this requirement.

// Castle.Core (used by NSubstitute) requires type arguments of proxied generic interfaces to be
// publicly accessible when the interface comes from a strong-named assembly. Making these types
// public satisfies that requirement without needing the InternalsVisibleTo key for Castle.Core.
public sealed record TestBatchEvent(string Kind);
public sealed class TestEntityA { public string Value { get; init; } = ""; }
public sealed class TestEntityB { public int Number { get; init; } }

/// <summary>
/// Unit tests for <see cref="ReadModelBatchConsumer{TEvent}"/> accumulation and dispatch logic.
/// EFCore.BulkExtensions requires a live Postgres provider, so the write path is hidden behind
/// <see cref="IReadBulkWriter"/> and substituted here. The DbContext factory returns an
/// InMemory context (supports no-op transactions).
/// </summary>
[TestFixture]
public sealed class ReadModelBatchConsumerBaseTests
{
    // ---------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Concrete consumer for testing. Kind controls which entity types are projected:
    /// "both" → TestEntityA + TestEntityB, "a-only" → TestEntityA, anything else → empty.
    /// </summary>
    private sealed class TestBatchConsumer(
        IDbContextFactory<ReadDbContext> factory,
        IReadBulkWriter writer)
        : ReadModelBatchConsumer<TestBatchEvent>(factory, writer, NullLogger.Instance)
    {
        protected override IEnumerable<object> Project(TestBatchEvent evt) => evt.Kind switch
        {
            "both"   => [new TestEntityA { Value = evt.Kind }, new TestEntityB { Number = 1 }],
            "a-only" => [new TestEntityA { Value = evt.Kind }],
            _        => [],
        };
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IDbContextFactory<ReadDbContext> BuildFactory()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory silently ignores transactions by default, but EF Core elevates the
            // TransactionIgnoredWarning to an exception in stricter configurations.
            // Suppress it here — the tests verify the base class passes the transaction
            // to IReadBulkWriter, not that the transaction semantics work end-to-end.
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FakeDbContextFactory(opts);
    }

    private sealed class FakeDbContextFactory(DbContextOptions<ReadDbContext> opts)
        : IDbContextFactory<ReadDbContext>
    {
        public ReadDbContext CreateDbContext() => new(opts);

        public ValueTask<ReadDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ReadDbContext(opts));
    }

    /// <summary>
    /// Builds a substituted <see cref="ConsumeContext{T}"/> wrapping a real
    /// <see cref="FakeBatch{T}"/>. The batch type itself is a concrete class because
    /// NSubstitute can't proxy interfaces with private type arguments (Castle.Core constraint).
    /// </summary>
    private static ConsumeContext<Batch<TestBatchEvent>> BuildBatchContext(
        params TestBatchEvent[] events)
    {
        // Build the FakeBatch BEFORE creating the substitute so that the internal
        // Substitute.For<ConsumeContext<TestBatchEvent>>() calls in FakeBatch's ctor don't
        // leave NSubstitute's thread-local state in an unexpected position when Returns() runs.
        var batch = new FakeBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<TestBatchEvent>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    /// <summary>
    /// Minimal concrete <see cref="Batch{T}"/> implementation. Concrete (not a proxy) so
    /// Castle.Core's proxy generation constraint is bypassed entirely.
    /// The per-message <see cref="ConsumeContext{T}"/> is a NSubstitute proxy — acceptable
    /// because <see cref="TestBatchEvent"/> is <c>internal</c> at namespace scope.
    /// </summary>
    private sealed class FakeBatch(TestBatchEvent[] events) : Batch<TestBatchEvent>
    {
        private readonly ConsumeContext<TestBatchEvent>[] _messages = events
            .Select(e =>
            {
                var m = Substitute.For<ConsumeContext<TestBatchEvent>>();
                m.Message.Returns(e);
                return m;
            })
            .ToArray();

        public BatchCompletionMode Mode => BatchCompletionMode.Time;
        public DateTime FirstMessageReceived => DateTime.UtcNow;
        public DateTime LastMessageReceived => DateTime.UtcNow;
        public ConsumeContext<TestBatchEvent> this[int i] => _messages[i];
        public int Length => _messages.Length;

        public IEnumerator<ConsumeContext<TestBatchEvent>> GetEnumerator() =>
            ((IEnumerable<ConsumeContext<TestBatchEvent>>)_messages).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEntityType_WriterCalledWithCorrectType()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        await consumer.Consume(BuildBatchContext(
            new TestBatchEvent("a-only"), new TestBatchEvent("a-only")));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.Count == 1 && d.ContainsKey(typeof(TestEntityA))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MultipleEntityTypes_WriterCalledWithBothTypes()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        await consumer.Consume(BuildBatchContext(new TestBatchEvent("both")));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.Count == 2
                && d.ContainsKey(typeof(TestEntityA))
                && d.ContainsKey(typeof(TestEntityB))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EntitiesAccumulatedAcrossMessages_SameTypeMergedIntoBucket()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        await consumer.Consume(BuildBatchContext(
            new TestBatchEvent("a-only"),
            new TestBatchEvent("a-only"),
            new TestBatchEvent("a-only")));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d[typeof(TestEntityA)].Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MixedBatch_EachTypeBucketHasCorrectCount()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        // 2 "both" → 2×EntityA + 2×EntityB; 1 "a-only" → 1×EntityA; total: 3×A, 2×B
        await consumer.Consume(BuildBatchContext(
            new TestBatchEvent("both"),
            new TestBatchEvent("both"),
            new TestBatchEvent("a-only")));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d[typeof(TestEntityA)].Count == 3
                && d[typeof(TestEntityB)].Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllProjectionsEmpty_WriterNotCalled()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        await consumer.Consume(BuildBatchContext(
            new TestBatchEvent("none"), new TestBatchEvent("none")));

        await writer.DidNotReceive().WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyBatch_WriterNotCalled()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new TestBatchConsumer(BuildFactory(), writer);

        await consumer.Consume(BuildBatchContext());

        await writer.DidNotReceive().WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WriterReceivesNonNullDbContextAndTransaction()
    {
        ReadDbContext? capturedDb = null;
        IDbContextTransaction? capturedTx = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer
            .When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                capturedDb = call.ArgAt<ReadDbContext>(0);
                capturedTx = call.ArgAt<IDbContextTransaction>(1);
            });

        var consumer = new TestBatchConsumer(BuildFactory(), writer);
        await consumer.Consume(BuildBatchContext(new TestBatchEvent("a-only")));

        capturedDb.ShouldNotBeNull();
        capturedTx.ShouldNotBeNull();
    }
}
