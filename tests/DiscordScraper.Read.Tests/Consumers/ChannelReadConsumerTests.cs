using System.Collections;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using MassTransit;
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
    public bool IsPresent { get; init; } = true;
    public string CurrentState { get; init; } = string.Empty;
    public DateTimeOffset UpdatedOn { get; set; }
    public Guid CorrelationId => Guid.NewGuid();
}

[TestFixture]
public sealed class ChannelReadConsumerTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

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
            CurrentState = "Active", UpdatedOn = _testTime,
        };

    private static ChannelReadConsumer BuildConsumer(IBulkWriter<ReadChannel> writer) =>
        new(new BatchProjectionPipeline<ChannelChanged, ReadChannel>(
            new MapperlyChannelChangedProjector(),
            writer,
            NullLogger<BatchProjectionPipeline<ChannelChanged, ReadChannel>>.Instance));

    // ---------------------------------------------------------------------------
    // Projector-level tests (pure event-in / entity-out, no consumer overhead)
    // ---------------------------------------------------------------------------

    [Test]
    public void Projector_SingleEvent_YieldsOneReadChannel()
    {
        var projector = new MapperlyChannelChangedProjector();
        var result = projector.Project(MakeEvent(channelId: 42L)).ToList();
        result.Count.ShouldBe(1);
        result[0].ChannelId.ShouldBe(42L);
    }

    [Test]
    public void Projector_FieldMapping_AllScalarFieldsProjectedCorrectly()
    {
        var projector = new MapperlyChannelChangedProjector();
        var result = projector.Project(MakeEvent(channelId: 999L, guildId: 888L, name: "dev-chat",
            topic: "all code", channelType: 11, parentId: 12345L)).Single();

        result.ChannelId.ShouldBe(999L);
        result.GuildId.ShouldBe(888L);
        result.Name.ShouldBe("dev-chat");
        result.Topic.ShouldBe("all code");
        result.Type.ShouldBe((short)11);
        result.ParentId.ShouldBe(12345L);
        result.UpdatedAt.ShouldBe(_testTime);
    }

    [Test]
    public void Projector_NullTopic_MapsToNullOnReadChannel()
    {
        var projector = new MapperlyChannelChangedProjector();
        projector.Project(MakeEvent(topic: null)).Single().Topic.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------
    // Consumer-level tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEvent_WriterReceivesOneReadChannel()
    {
        var writer = Substitute.For<IBulkWriter<ReadChannel>>();
        var consumer = BuildConsumer(writer);

        await consumer.Consume(BuildBatchContext(MakeEvent()));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<ReadChannel>>(e => e.Count() == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThreeEvents_WriterReceivesThreeReadChannels()
    {
        var writer = Substitute.For<IBulkWriter<ReadChannel>>();
        var consumer = BuildConsumer(writer);

        await consumer.Consume(BuildBatchContext(
            MakeEvent(channelId: 1L),
            MakeEvent(channelId: 2L),
            MakeEvent(channelId: 3L)));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<ReadChannel>>(e => e.Count() == 3),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FieldMapping_AllScalarFieldsProjectedCorrectly()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IBulkWriter<ReadChannel>>();
        writer.When(w => w.WriteAsync(Arg.Any<IEnumerable<ReadChannel>>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IEnumerable<ReadChannel>>(0).First());

        var evt = MakeEvent(channelId: 999L, guildId: 888L, name: "dev-chat", topic: "all code");
        await BuildConsumer(writer).Consume(BuildBatchContext(evt));

        captured.ShouldNotBeNull();
        captured.ChannelId.ShouldBe(999L);
        captured.GuildId.ShouldBe(888L);
        captured.Name.ShouldBe("dev-chat");
        captured.Topic.ShouldBe("all code");
        captured.UpdatedAt.ShouldBe(_testTime);
        captured.Type.ShouldBe((short)0);
        captured.ParentId.ShouldBeNull();
    }

    [Test]
    public async Task PublicThread_TypeAndParentIdMappedCorrectly()
    {
        ReadChannel? captured = null;

        var writer = Substitute.For<IBulkWriter<ReadChannel>>();
        writer.When(w => w.WriteAsync(Arg.Any<IEnumerable<ReadChannel>>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IEnumerable<ReadChannel>>(0).First());

        await BuildConsumer(writer).Consume(BuildBatchContext(MakeEvent(channelType: 11, parentId: 12345L)));

        captured.ShouldNotBeNull();
        captured.Type.ShouldBe((short)11);
        captured.ParentId.ShouldBe(12345L);
    }
}
