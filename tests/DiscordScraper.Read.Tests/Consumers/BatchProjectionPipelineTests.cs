using System.Collections;
using DiscordScraper.Read.Consumers;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Consumers;

// NSubstitute/Castle.Core can only create proxies for generic interfaces when all type
// arguments are publicly accessible. Because ConsumeContext<T> comes from strong-named
// MassTransit.Abstractions, T must be visible to DynamicProxyGenAssembly2. Declaring the
// test event/entity types at namespace scope (public) satisfies this requirement.
public sealed record TestBatchEvent(string Kind);
public sealed class TestEntityA { public string Value { get; init; } = ""; }

/// <summary>
/// Unit tests for <see cref="BatchProjectionPipeline{TEvent,TEntity}"/> accumulation and
/// dispatch logic. The write path is hidden behind <see cref="IBulkWriter{TEntity}"/> and
/// substituted here.
/// </summary>
[TestFixture]
public sealed class BatchProjectionPipelineTests
{
    private sealed class TestProjector(string emitKind = "emit")
        : IBatchProjector<TestBatchEvent, TestEntityA>
    {
        public IEnumerable<TestEntityA> Project(TestBatchEvent evt)
        {
            if (evt.Kind == emitKind)
                yield return new TestEntityA { Value = evt.Kind };
        }
    }

    private static ConsumeContext<Batch<TestBatchEvent>> BuildBatchContext(
        params TestBatchEvent[] events)
    {
        var batch = new FakeBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<TestBatchEvent>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

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

    private static BatchProjectionPipeline<TestBatchEvent, TestEntityA> BuildPipeline(
        IBulkWriter<TestEntityA> writer, string emitKind = "emit") =>
        new(new TestProjector(emitKind), writer, NullLogger<BatchProjectionPipeline<TestBatchEvent, TestEntityA>>.Instance);

    [Test]
    public async Task SingleEvent_WriterCalledWithOneEntity()
    {
        var writer = Substitute.For<IBulkWriter<TestEntityA>>();
        var pipeline = BuildPipeline(writer);

        await pipeline.Project(BuildBatchContext(new TestBatchEvent("emit")));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<TestEntityA>>(e => e.Count() == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThreeEvents_WriterReceivesThreeEntities()
    {
        var writer = Substitute.For<IBulkWriter<TestEntityA>>();
        var pipeline = BuildPipeline(writer);

        await pipeline.Project(BuildBatchContext(
            new TestBatchEvent("emit"),
            new TestBatchEvent("emit"),
            new TestBatchEvent("emit")));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<TestEntityA>>(e => e.Count() == 3),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllProjectionsEmpty_WriterNotCalled()
    {
        var writer = Substitute.For<IBulkWriter<TestEntityA>>();
        var pipeline = BuildPipeline(writer, emitKind: "emit");

        await pipeline.Project(BuildBatchContext(
            new TestBatchEvent("none"), new TestBatchEvent("none")));

        await writer.DidNotReceive().WriteAsync(
            Arg.Any<IEnumerable<TestEntityA>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmptyBatch_WriterNotCalled()
    {
        var writer = Substitute.For<IBulkWriter<TestEntityA>>();
        var pipeline = BuildPipeline(writer);

        await pipeline.Project(BuildBatchContext());

        await writer.DidNotReceive().WriteAsync(
            Arg.Any<IEnumerable<TestEntityA>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MixedBatch_OnlyEmittingEventsCountedInWrite()
    {
        var writer = Substitute.For<IBulkWriter<TestEntityA>>();
        var pipeline = BuildPipeline(writer);

        // 2 emitting + 1 silent → writer gets 2 entities
        await pipeline.Project(BuildBatchContext(
            new TestBatchEvent("emit"),
            new TestBatchEvent("none"),
            new TestBatchEvent("emit")));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<TestEntityA>>(e => e.Count() == 2),
            Arg.Any<CancellationToken>());
    }
}
