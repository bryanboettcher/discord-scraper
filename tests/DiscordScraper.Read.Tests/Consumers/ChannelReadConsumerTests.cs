using System.Collections;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Consumers;

// Castle.Core (used by NSubstitute) requires type arguments of proxied generic interfaces from
// strong-named assemblies to be publicly accessible. These test doubles are public for that reason.
public sealed class TestChannelChanged : ChannelChanged
{
    public long ChannelId { get; init; }
    public long GuildId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Topic { get; init; }
    public int ChannelType { get; init; }
    public long? ParentId { get; init; }
    public string CurrentState { get; init; } = string.Empty;
    public DateTimeOffset LastUpdatedAt { get; set; }
    public Guid CorrelationId => Guid.NewGuid();
}

[TestFixture]
public sealed class ChannelReadConsumerTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IDbContextFactory<ReadDbContext> BuildFactory()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FakeReadDbContextFactory(opts);
    }

    private sealed class FakeReadDbContextFactory(DbContextOptions<ReadDbContext> opts)
        : IDbContextFactory<ReadDbContext>
    {
        public ReadDbContext CreateDbContext() => new(opts);

        public ValueTask<ReadDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ReadDbContext(opts));
    }

    private static ConsumeContext<Batch<ChannelChanged>> BuildBatchContext(
        params ChannelChanged[] events)
    {
        var batch = new FakeChannelBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<ChannelChanged>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private sealed class FakeChannelBatch(ChannelChanged[] events) : Batch<ChannelChanged>
    {
        private readonly ConsumeContext<ChannelChanged>[] _messages = events
            .Select(e =>
            {
                var m = Substitute.For<ConsumeContext<ChannelChanged>>();
                m.Message.Returns(e);
                return m;
            })
            .ToArray();

        public BatchCompletionMode Mode => BatchCompletionMode.Time;
        public DateTime FirstMessageReceived => DateTime.UtcNow;
        public DateTime LastMessageReceived => DateTime.UtcNow;
        public ConsumeContext<ChannelChanged> this[int i] => _messages[i];
        public int Length => _messages.Length;

        public IEnumerator<ConsumeContext<ChannelChanged>> GetEnumerator() =>
            ((IEnumerable<ConsumeContext<ChannelChanged>>)_messages).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static readonly DateTimeOffset _testTime =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TestChannelChanged MakeEvent(
        long channelId = 111L,
        long guildId = 222L,
        string name = "general",
        string? topic = null,
        int channelType = 0,
        long? parentId = null) =>
        new()
        {
            ChannelId = channelId, GuildId = guildId, Name = name, Topic = topic,
            ChannelType = channelType, ParentId = parentId,
            CurrentState = "Active", LastUpdatedAt = _testTime,
        };

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEvent_WriterReceivesOneReadChannel()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);

        await consumer.Consume(BuildBatchContext(MakeEvent()));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.ContainsKey(typeof(ReadChannel)) && d[typeof(ReadChannel)].Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThreeEvents_WriterReceivesThreeReadChannels()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);

        await consumer.Consume(BuildBatchContext(
            MakeEvent(channelId: 1L),
            MakeEvent(channelId: 2L),
            MakeEvent(channelId: 3L)));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d[typeof(ReadChannel)].Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NullTopic_MapsToNullOnReadChannel()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadChannel)dict[typeof(ReadChannel)][0];
            });

        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);
        await consumer.Consume(BuildBatchContext(MakeEvent(topic: null)));

        captured.ShouldNotBeNull();
        captured.Topic.ShouldBeNull();
    }

    [Test]
    public async Task NonNullTopic_MapsToReadChannel()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadChannel)dict[typeof(ReadChannel)][0];
            });

        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);
        await consumer.Consume(BuildBatchContext(MakeEvent(topic: "server announcements")));

        captured.ShouldNotBeNull();
        captured.Topic.ShouldBe("server announcements");
    }

    [Test]
    public async Task TextChannel_TypeAndParentIdMappedCorrectly()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadChannel)dict[typeof(ReadChannel)][0];
            });

        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);
        // Type=0 (text), ParentId=null (top-level channel)
        await consumer.Consume(BuildBatchContext(MakeEvent(channelType: 0, parentId: null)));

        captured.ShouldNotBeNull();
        captured.Type.ShouldBe((short)0);
        captured.ParentId.ShouldBeNull();
    }

    [Test]
    public async Task PublicThread_TypeAndParentIdMappedCorrectly()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadChannel)dict[typeof(ReadChannel)][0];
            });

        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);
        // Type=11 (PUBLIC_THREAD), ParentId=12345 (parent text channel)
        await consumer.Consume(BuildBatchContext(MakeEvent(channelType: 11, parentId: 12345L)));

        captured.ShouldNotBeNull();
        captured.Type.ShouldBe((short)11);
        captured.ParentId.ShouldBe(12345L);
    }

    [Test]
    public async Task FieldMapping_AllScalarFieldsProjectedCorrectly()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadChannel)dict[typeof(ReadChannel)][0];
            });

        var evt = MakeEvent(channelId: 999L, guildId: 888L, name: "dev-chat", topic: "all code");
        var consumer = new ChannelReadConsumer(BuildFactory(), writer, NullLogger<ChannelReadConsumer>.Instance);
        await consumer.Consume(BuildBatchContext(evt));

        captured.ShouldNotBeNull();
        captured.ChannelId.ShouldBe(999L);
        captured.GuildId.ShouldBe(888L);
        captured.Name.ShouldBe("dev-chat");
        captured.Topic.ShouldBe("all code");
        // LastUpdatedAt → UpdatedAt name-mismatch mapping
        captured.UpdatedAt.ShouldBe(_testTime);
        // ChannelType → Type name-mismatch mapping
        captured.Type.ShouldBe((short)0);
        captured.ParentId.ShouldBeNull();
    }
}
