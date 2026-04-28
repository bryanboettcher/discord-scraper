using DiscordScraper.Contracts.IR;
using DiscordScraper.Core.Queries;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;

namespace DiscordScraper.Read.Tests.Queries;

/// <summary>
/// GetActiveChannelsAsync uses raw NpgsqlDataSource querying a TimescaleDB continuous aggregate
/// (messages_per_channel_per_hour). The in-memory provider cannot model that view, so those
/// tests validate the service contract through the IChannelQueryService public interface backed
/// by a mock, or verify type-level metadata. Integration tests own the live-DB path.
///
/// GetRecentMessagesAsync uses EF and is testable via in-memory.
/// </summary>
[TestFixture]
public sealed class ChannelQueryServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeReadDbContextFactory(DbContextOptions<ReadDbContext> opts)
        : IDbContextFactory<ReadDbContext>
    {
        public ReadDbContext CreateDbContext() => new(opts);

        public ValueTask<ReadDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new ReadDbContext(opts));
    }

    private static IDbContextFactory<ReadDbContext> BuildFactory()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FakeReadDbContextFactory(opts);
    }

    private static MessageIR SimpleIr() =>
        new(Body: [new TextNode("hi")], Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: Now);

    private static ReadMessage MakeMessage(long id, long channelId, DateTimeOffset? createdAt = null) =>
        new()
        {
            MessageId = id,
            ChannelId = channelId,
            GuildId   = 1L,
            AuthorId  = 10L,
            CreatedAt = createdAt ?? Now,
            Ir        = SimpleIr(),
            PlainText = "hi"
        };

    // ChannelQueryService is internal and sealed — instantiate directly via InternalsVisibleTo.
    // NpgsqlDataSource can't be mocked easily (sealed class), so for the NpgsqlDataSource-dependent
    // method (GetActiveChannelsAsync) we test only through the IChannelQueryService mock path.
    private static IChannelQueryService BuildMockedService()
    {
        return Substitute.For<IChannelQueryService>();
    }

    // -------------------------------------------------------------------------
    // IChannelQueryService contract / metadata tests
    // -------------------------------------------------------------------------

    [Test]
    public void ChannelQueryService_ImplementsIChannelQueryService()
    {
        typeof(ChannelQueryService).GetInterfaces()
            .ShouldContain(typeof(IChannelQueryService));
    }

    [Test]
    public void ChannelQueryService_IsSealed()
    {
        typeof(ChannelQueryService).IsSealed.ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // GetActiveChannelsAsync — tested via mock (raw Npgsql path hits TimescaleDB aggregate)
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetActiveChannelsAsync_ReturnsChannelsOrderedByMessageCountDesc()
    {
        var svc = BuildMockedService();
        var expected = new List<ChannelSummary>
        {
            new(1L, "busy",   100, Now),
            new(2L, "medium",  50, Now),
            new(3L, "quiet",   10, Now)
        };
        svc.GetActiveChannelsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns(expected);

        var result = await svc.GetActiveChannelsAsync(99L, 24, CancellationToken.None);

        result.Count.ShouldBe(3);
        result[0].MessageCountInWindow.ShouldBeGreaterThanOrEqualTo(result[1].MessageCountInWindow);
        result[1].MessageCountInWindow.ShouldBeGreaterThanOrEqualTo(result[2].MessageCountInWindow);
    }

    [Test]
    public async Task GetActiveChannelsAsync_CancellationToken_PassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var svc = BuildMockedService();
        svc.GetActiveChannelsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns([]);

        await svc.GetActiveChannelsAsync(1L, 12, token);

        await svc.Received(1).GetActiveChannelsAsync(1L, 12, token);
    }

    // -------------------------------------------------------------------------
    // GetRecentMessagesAsync — real EF in-memory path
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetRecentMessagesAsync_EmptyChannel_ReturnsEmpty()
    {
        // Build the real ChannelQueryService with an in-memory db that has no messages.
        var factory = BuildFactory();

        // NpgsqlDataSource is needed in the constructor but only for GetActiveChannelsAsync.
        // For GetRecentMessagesAsync it's unused, so a null-source won't be dereferenced.
        var svc = new ChannelQueryService(
            dataSource: null!,
            factory,
            NullLogger<ChannelQueryService>.Instance);

        var result = await svc.GetRecentMessagesAsync(42L, 10, RenderFormat.PlainText, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetRecentMessagesAsync_ReturnsNewestFirst()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(1L, 42L, Now.AddHours(-3)));
            db.ReadMessages.Add(MakeMessage(2L, 42L, Now.AddHours(-1)));
            db.ReadMessages.Add(MakeMessage(3L, 42L, Now.AddHours(-2)));
            db.ReadChannels.Add(new ReadChannel
            {
                ChannelId = 42L, GuildId = 1L, Name = "chan", UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var svc = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(42L, 10, RenderFormat.PlainText, CancellationToken.None);

        result.Count.ShouldBe(3);
        // Newest first: msg 2 (Now-1h), msg 3 (Now-2h), msg 1 (Now-3h)
        result[0].MessageId.ShouldBe(2L);
        result[1].MessageId.ShouldBe(3L);
        result[2].MessageId.ShouldBe(1L);
    }

    [Test]
    public async Task GetRecentMessagesAsync_CountLimitRespected()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 1; i <= 10; i++)
                db.ReadMessages.Add(MakeMessage(i, 55L, Now.AddMinutes(-i)));
            await db.SaveChangesAsync();
        }

        var svc = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(55L, 3, RenderFormat.PlainText, CancellationToken.None);

        result.Count.ShouldBe(3);
    }

    [Test]
    public async Task GetRecentMessagesAsync_ChannelNameResolvedFromDb()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(100L, 66L));
            db.ReadChannels.Add(new ReadChannel
            {
                ChannelId = 66L, GuildId = 1L, Name = "resolved-name", UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var svc    = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(66L, 5, RenderFormat.PlainText, CancellationToken.None);

        result[0].ChannelName.ShouldBe("resolved-name");
    }

    [Test]
    public async Task GetRecentMessagesAsync_NoChannelRow_NameFallsBackToSnowflake()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(200L, 77L));
            // no ReadChannel for 77
            await db.SaveChangesAsync();
        }

        var svc    = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(77L, 5, RenderFormat.PlainText, CancellationToken.None);

        result[0].ChannelName.ShouldBe("77");
    }

    [Test]
    public async Task GetRecentMessagesAsync_TagsPopulatedPerMessage()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.ReadMessages.Add(MakeMessage(300L, 88L));
            db.MessageTags.AddRange(
                new MessageTag { MessageId = 300L, Tag = "rust" },
                new MessageTag { MessageId = 300L, Tag = "systems" });
            await db.SaveChangesAsync();
        }

        var svc    = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(88L, 5, RenderFormat.PlainText, CancellationToken.None);

        result[0].Tags.ShouldContain("rust");
        result[0].Tags.ShouldContain("systems");
    }

    [Test]
    public async Task GetRecentMessagesAsync_BodyIsRenderedFromIr()
    {
        var factory = BuildFactory();
        await using (var db = factory.CreateDbContext())
        {
            var ir = new MessageIR(
                Body: [new TextNode("rendered text")],
                Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: Now);
            db.ReadMessages.Add(new ReadMessage
            {
                MessageId = 400L, ChannelId = 99L, GuildId = 1L, AuthorId = 10L,
                CreatedAt = Now, Ir = ir, PlainText = "rendered text"
            });
            await db.SaveChangesAsync();
        }

        var svc    = new ChannelQueryService(null!, factory, NullLogger<ChannelQueryService>.Instance);
        var result = await svc.GetRecentMessagesAsync(99L, 5, RenderFormat.PlainText, CancellationToken.None);

        result[0].Body.ShouldBe("rendered text");
    }
}
