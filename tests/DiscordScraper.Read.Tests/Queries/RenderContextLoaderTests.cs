using DiscordScraper.Contracts.IR;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using DiscordScraper.Read.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DiscordScraper.Read.Tests.Queries;

[TestFixture]
public sealed class RenderContextLoaderTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ReadDbContext BuildDb()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ReadDbContext(opts);
    }

    private static MessageIR EmptyIr() =>
        new(Body: [], Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: DateTimeOffset.UtcNow);

    private static MessageIR IrWithBody(params MessageNode[] nodes) =>
        new(Body: nodes, Attachments: [], Embeds: [], ReplyTo: null, CapturedAt: DateTimeOffset.UtcNow);

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task LoadAsync_EmptyIr_ReturnsEmptyDictionaries()
    {
        await using var db = BuildDb();

        var ctx = await RenderContextLoader.LoadAsync(EmptyIr(), db, CancellationToken.None);

        ctx.ChannelNames.ShouldBeEmpty();
        ctx.RoleNames.ShouldBeEmpty();
        ctx.UserNames.ShouldBeEmpty();
        ctx.EmojiNames.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadAsync_IrWithChannelRef_ResolvesFromDb()
    {
        await using var db = BuildDb();
        db.ReadChannels.Add(new ReadChannel
        {
            ChannelId = 111L,
            GuildId   = 1L,
            Name      = "general",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var ir  = IrWithBody(new ChannelRefNode(ChannelId: 111L, Fallback: "old-name"));
        var ctx = await RenderContextLoader.LoadAsync(ir, db, CancellationToken.None);

        ctx.ChannelNames.ShouldContainKey(111L);
        ctx.ChannelNames[111L].ShouldBe("general");
    }

    [Test]
    public async Task LoadAsync_ChannelRefInsideFormattingNode_WalksRecursively()
    {
        await using var db = BuildDb();
        db.ReadChannels.Add(new ReadChannel
        {
            ChannelId = 222L,
            GuildId   = 1L,
            Name      = "announcements",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var channelRef    = new ChannelRefNode(222L, null);
        var formattingNode = new FormattingNode(FormattingKind.Bold, [channelRef]);
        var ir            = IrWithBody(formattingNode);

        var ctx = await RenderContextLoader.LoadAsync(ir, db, CancellationToken.None);

        ctx.ChannelNames.ShouldContainKey(222L);
    }

    [Test]
    public async Task LoadAsync_ChannelRefInsideQuoteNode_WalksRecursively()
    {
        await using var db = BuildDb();
        db.ReadChannels.Add(new ReadChannel
        {
            ChannelId = 333L,
            GuildId   = 1L,
            Name      = "quotes-channel",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var quoteNode = new QuoteNode([new ChannelRefNode(333L, null)]);
        var ir        = IrWithBody(quoteNode);

        var ctx = await RenderContextLoader.LoadAsync(ir, db, CancellationToken.None);

        ctx.ChannelNames.ShouldContainKey(333L);
    }

    [Test]
    public async Task LoadAsync_ChannelRefInsideLinkNode_WalksRecursively()
    {
        await using var db = BuildDb();
        db.ReadChannels.Add(new ReadChannel
        {
            ChannelId = 444L,
            GuildId   = 1L,
            Name      = "link-channel",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var linkNode = new LinkNode("https://example.com", [new ChannelRefNode(444L, null)]);
        var ir       = IrWithBody(linkNode);

        var ctx = await RenderContextLoader.LoadAsync(ir, db, CancellationToken.None);

        ctx.ChannelNames.ShouldContainKey(444L);
    }

    [Test]
    public async Task LoadAsync_MissingChannelId_NotInDict_FallbackFromNode()
    {
        // Channel 999 is not in the DB. The renderer uses node's Fallback string — the
        // loader just leaves it absent from ChannelNames.
        await using var db = BuildDb();

        var ir  = IrWithBody(new ChannelRefNode(999L, Fallback: "deleted-channel"));
        var ctx = await RenderContextLoader.LoadAsync(ir, db, CancellationToken.None);

        ctx.ChannelNames.ShouldNotContainKey(999L);
    }

    [Test]
    public async Task LoadManyAsync_MultipleDistinctChannelRefs_SingleBatchQuery()
    {
        await using var db = BuildDb();
        db.ReadChannels.AddRange(
            new ReadChannel { ChannelId = 10L, GuildId = 1L, Name = "alpha", UpdatedAt = DateTimeOffset.UtcNow },
            new ReadChannel { ChannelId = 20L, GuildId = 1L, Name = "beta",  UpdatedAt = DateTimeOffset.UtcNow },
            new ReadChannel { ChannelId = 30L, GuildId = 1L, Name = "gamma", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var ir1 = IrWithBody(new ChannelRefNode(10L, null), new ChannelRefNode(20L, null));
        var ir2 = IrWithBody(new ChannelRefNode(30L, null));

        var ctx = await RenderContextLoader.LoadManyAsync([ir1, ir2], db, CancellationToken.None);

        ctx.ChannelNames.Count.ShouldBe(3);
        ctx.ChannelNames[10L].ShouldBe("alpha");
        ctx.ChannelNames[20L].ShouldBe("beta");
        ctx.ChannelNames[30L].ShouldBe("gamma");
    }

    [Test]
    public async Task LoadManyAsync_DuplicateChannelIdAcrossIrs_DeduplicatedInResult()
    {
        await using var db = BuildDb();
        db.ReadChannels.Add(new ReadChannel
        {
            ChannelId = 55L,
            GuildId   = 1L,
            Name      = "shared",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var ir1 = IrWithBody(new ChannelRefNode(55L, null));
        var ir2 = IrWithBody(new ChannelRefNode(55L, null));

        var ctx = await RenderContextLoader.LoadManyAsync([ir1, ir2], db, CancellationToken.None);

        // Same channel_id deduplicated; appears once in the dictionary.
        ctx.ChannelNames.Count.ShouldBe(1);
        ctx.ChannelNames[55L].ShouldBe("shared");
    }

    [Test]
    public async Task LoadManyAsync_EmptyIrList_ReturnsEmptyContext()
    {
        await using var db = BuildDb();

        var ctx = await RenderContextLoader.LoadManyAsync([], db, CancellationToken.None);

        ctx.ChannelNames.ShouldBeEmpty();
    }
}
