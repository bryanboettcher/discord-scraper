using System.Collections;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Mapping;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordScraper.Read.Tests.Consumers;

// Castle.Core (used by NSubstitute) requires type arguments of proxied generic interfaces from
// strong-named assemblies to be publicly accessible. These test doubles are public for that reason.
public sealed class TestGuildChanged : GuildChanged
{
    public long GuildId { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<GuildRole> Roles { get; init; } = [];
    public bool IsPresent { get; init; } = true;
    public string CurrentState { get; init; } = string.Empty;
    public DateTimeOffset UpdatedOn { get; set; }
    public Guid CorrelationId => Guid.NewGuid();
}

[TestFixture]
public sealed class GuildReadConsumerTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ConsumeContext<Batch<GuildChanged>> BuildBatchContext(
        params GuildChanged[] events)
    {
        var batch = new FakeGuildBatch(events);
        var ctx = Substitute.For<ConsumeContext<Batch<GuildChanged>>>();
        ctx.Message.Returns(batch);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private sealed class FakeGuildBatch(GuildChanged[] events) : Batch<GuildChanged>
    {
        private readonly ConsumeContext<GuildChanged>[] _messages = events
            .Select(e =>
            {
                var m = Substitute.For<ConsumeContext<GuildChanged>>();
                m.Message.Returns(e);
                return m;
            })
            .ToArray();

        public BatchCompletionMode Mode => BatchCompletionMode.Time;
        public DateTime FirstMessageReceived => DateTime.UtcNow;
        public DateTime LastMessageReceived => DateTime.UtcNow;
        public ConsumeContext<GuildChanged> this[int i] => _messages[i];
        public int Length => _messages.Length;

        public IEnumerator<ConsumeContext<GuildChanged>> GetEnumerator() =>
            ((IEnumerable<ConsumeContext<GuildChanged>>)_messages).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static readonly DateTimeOffset _testTime =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TestGuildChanged MakeEvent(
        long guildId = 555L,
        string name = "My Guild") =>
        new() { GuildId = guildId, Name = name, CurrentState = "Active", UpdatedOn = _testTime };

    private static GuildReadConsumer BuildConsumer(IBulkWriter<ReadGuild> writer) =>
        new(new MapperlyGuildChangedProjector(), writer, NullLogger<GuildReadConsumer>.Instance);

    // ---------------------------------------------------------------------------
    // Projector-level tests (pure event-in / entity-out, no consumer overhead)
    // ---------------------------------------------------------------------------

    [Test]
    public void Projector_SingleEvent_YieldsOneReadGuild()
    {
        var projector = new MapperlyGuildChangedProjector();
        var result = projector.Project(MakeEvent(guildId: 42L)).ToList();
        result.Count.ShouldBe(1);
        result[0].GuildId.ShouldBe(42L);
    }

    [Test]
    public void Projector_FieldMapping_AllFieldsProjectedCorrectly()
    {
        var projector = new MapperlyGuildChangedProjector();
        var result = projector.Project(MakeEvent(guildId: 42L, name: "Awesome Guild")).Single();

        result.GuildId.ShouldBe(42L);
        result.Name.ShouldBe("Awesome Guild");
        result.UpdatedAt.ShouldBe(_testTime);
    }

    // ---------------------------------------------------------------------------
    // Consumer-level tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEvent_WriterReceivesOneReadGuild()
    {
        var writer = Substitute.For<IBulkWriter<ReadGuild>>();
        await BuildConsumer(writer).Consume(BuildBatchContext(MakeEvent()));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<ReadGuild>>(e => e.Count() == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FiveEvents_WriterReceivesFiveReadGuilds()
    {
        var writer = Substitute.For<IBulkWriter<ReadGuild>>();
        await BuildConsumer(writer).Consume(BuildBatchContext(
            MakeEvent(guildId: 1L),
            MakeEvent(guildId: 2L),
            MakeEvent(guildId: 3L),
            MakeEvent(guildId: 4L),
            MakeEvent(guildId: 5L)));

        await writer.Received(1).WriteAsync(
            Arg.Is<IEnumerable<ReadGuild>>(e => e.Count() == 5),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdatedAt_MapsFromUpdatedOn()
    {
        ReadGuild? captured = null;

        var writer = Substitute.For<IBulkWriter<ReadGuild>>();
        writer.When(w => w.WriteAsync(Arg.Any<IEnumerable<ReadGuild>>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IEnumerable<ReadGuild>>(0).First());

        var specificTime = new DateTimeOffset(2025, 3, 15, 9, 30, 0, TimeSpan.Zero);
        await BuildConsumer(writer).Consume(BuildBatchContext(
            new TestGuildChanged { GuildId = 777L, Name = "Test Guild", CurrentState = "Active", UpdatedOn = specificTime }));

        captured.ShouldNotBeNull();
        captured.UpdatedAt.ShouldBe(specificTime);
    }

    [Test]
    public async Task FieldMapping_AllFieldsProjectedCorrectly()
    {
        ReadGuild? captured = null;

        var writer = Substitute.For<IBulkWriter<ReadGuild>>();
        writer.When(w => w.WriteAsync(Arg.Any<IEnumerable<ReadGuild>>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.ArgAt<IEnumerable<ReadGuild>>(0).First());

        await BuildConsumer(writer).Consume(BuildBatchContext(MakeEvent(guildId: 42L, name: "Awesome Guild")));

        captured.ShouldNotBeNull();
        captured.GuildId.ShouldBe(42L);
        captured.Name.ShouldBe("Awesome Guild");
        captured.UpdatedAt.ShouldBe(_testTime);
    }
}
