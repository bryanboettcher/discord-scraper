using System.Collections;
using DiscordScraper.Contracts.Events.Guild;
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
public sealed class TestGuildChanged : GuildChanged
{
    public long GuildId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CurrentState { get; init; } = string.Empty;
    public DateTimeOffset LastUpdatedAt { get; set; }
    public Guid CorrelationId => Guid.NewGuid();
}

[TestFixture]
public sealed class GuildReadConsumerTests
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
        new() { GuildId = guildId, Name = name, CurrentState = "Active", LastUpdatedAt = _testTime };

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Test]
    public async Task SingleEvent_WriterReceivesOneReadGuild()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new GuildReadConsumer(BuildFactory(), writer, NullLogger<GuildReadConsumer>.Instance);

        await consumer.Consume(BuildBatchContext(MakeEvent()));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d.ContainsKey(typeof(ReadGuild)) && d[typeof(ReadGuild)].Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FiveEvents_WriterReceivesFiveReadGuilds()
    {
        var writer = Substitute.For<IReadBulkWriter>();
        var consumer = new GuildReadConsumer(BuildFactory(), writer, NullLogger<GuildReadConsumer>.Instance);

        await consumer.Consume(BuildBatchContext(
            MakeEvent(guildId: 1L),
            MakeEvent(guildId: 2L),
            MakeEvent(guildId: 3L),
            MakeEvent(guildId: 4L),
            MakeEvent(guildId: 5L)));

        await writer.Received(1).WriteAsync(
            Arg.Any<ReadDbContext>(),
            Arg.Any<IDbContextTransaction>(),
            Arg.Is<IReadOnlyDictionary<Type, IList<object>>>(d =>
                d[typeof(ReadGuild)].Count == 5),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdatedAt_MapsFromLastUpdatedAt()
    {
        ReadGuild? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadGuild)dict[typeof(ReadGuild)][0];
            });

        var specificTime = new DateTimeOffset(2025, 3, 15, 9, 30, 0, TimeSpan.Zero);
        var consumer = new GuildReadConsumer(BuildFactory(), writer, NullLogger<GuildReadConsumer>.Instance);
        await consumer.Consume(BuildBatchContext(new TestGuildChanged { GuildId = 777L, Name = "Test Guild", CurrentState = "Active", LastUpdatedAt = specificTime }));

        captured.ShouldNotBeNull();
        captured.UpdatedAt.ShouldBe(specificTime);
    }

    [Test]
    public async Task FieldMapping_AllFieldsProjectedCorrectly()
    {
        ReadGuild? captured = null;

        var writer = Substitute.For<IReadBulkWriter>();
        writer.When(w => w.WriteAsync(
                Arg.Any<ReadDbContext>(),
                Arg.Any<IDbContextTransaction>(),
                Arg.Any<IReadOnlyDictionary<Type, IList<object>>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var dict = call.ArgAt<IReadOnlyDictionary<Type, IList<object>>>(2);
                captured = (ReadGuild)dict[typeof(ReadGuild)][0];
            });

        var consumer = new GuildReadConsumer(BuildFactory(), writer, NullLogger<GuildReadConsumer>.Instance);
        await consumer.Consume(BuildBatchContext(MakeEvent(guildId: 42L, name: "Awesome Guild")));

        captured.ShouldNotBeNull();
        captured.GuildId.ShouldBe(42L);
        captured.Name.ShouldBe("Awesome Guild");
        captured.UpdatedAt.ShouldBe(_testTime);
    }
}
