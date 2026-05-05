using DiscordScraper.Read.Consumers;
using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DiscordScraper.Read.Tests.Consumers;

/// <summary>
/// Unit tests for the PK-dedup logic inside <see cref="EfCoreBulkWriterStatics"/>.
/// DedupeByPrimaryKey is internal and exercised directly via InternalsVisibleTo.
/// All tests use InMemory + the real entity configurations so primary-key metadata matches production.
/// </summary>
[TestFixture]
public sealed class EfCoreBulkWriterTests
{
    private static ReadDbContext BuildDb()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ReadDbContext(opts);
    }

    // -------------------------------------------------------------------------
    // Single-column PK (ReadChannel.ChannelId)
    // -------------------------------------------------------------------------

    [Test]
    public void SingleEntity_NoDuplicate_ReturnedUnchanged()
    {
        using var db = BuildDb();
        var channel = new ReadChannel { ChannelId = 1L, Name = "general" };
        IList<object> input = [channel];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadChannel), input);

        result.Count.ShouldBe(1);
        result[0].ShouldBeSameAs(channel);
    }

    [Test]
    public void TwoEntities_SameSingleColumnPk_LastWins()
    {
        using var db = BuildDb();
        var first  = new ReadChannel { ChannelId = 42L, Name = "first"  };
        var second = new ReadChannel { ChannelId = 42L, Name = "second" };
        IList<object> input = [first, second];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadChannel), input);

        result.Count.ShouldBe(1);
        ((ReadChannel)result[0]).Name.ShouldBe("second");
    }

    [Test]
    public void TwoEntities_DifferentSingleColumnPk_BothKept()
    {
        using var db = BuildDb();
        IList<object> input =
        [
            new ReadChannel { ChannelId = 1L, Name = "a" },
            new ReadChannel { ChannelId = 2L, Name = "b" },
        ];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadChannel), input);

        result.Count.ShouldBe(2);
    }

    // -------------------------------------------------------------------------
    // Composite PK (MessageTag.(MessageId, Tag))
    // -------------------------------------------------------------------------

    [Test]
    public void TwoEntities_SameCompositePk_LastWins()
    {
        using var db = BuildDb();
        var first  = new MessageTag { MessageId = 100L, Tag = "csharp" };
        var second = new MessageTag { MessageId = 100L, Tag = "csharp" };
        IList<object> input = [first, second];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(MessageTag), input);

        result.Count.ShouldBe(1);
        result[0].ShouldBeSameAs(second);
    }

    [Test]
    public void TwoEntities_CompositePkDiffersOnOneColumn_BothKept()
    {
        using var db = BuildDb();
        IList<object> input =
        [
            new MessageTag { MessageId = 100L, Tag = "csharp" },
            new MessageTag { MessageId = 100L, Tag = "dotnet" },
        ];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(MessageTag), input);

        result.Count.ShouldBe(2);
    }

    [Test]
    public void TwoEntities_CompositePkDiffersOnBothColumns_BothKept()
    {
        using var db = BuildDb();
        IList<object> input =
        [
            new MessageTag { MessageId = 1L, Tag = "alpha" },
            new MessageTag { MessageId = 2L, Tag = "beta"  },
        ];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(MessageTag), input);

        result.Count.ShouldBe(2);
    }

    // -------------------------------------------------------------------------
    // Multiple entity types — each deduplicated independently
    // -------------------------------------------------------------------------

    [Test]
    public void MultipleTypes_EachDeduplicatedIndependently()
    {
        using var db = BuildDb();

        // ReadGuild: single PK; two rows with same GuildId → 1 survives
        var guild1  = new ReadGuild { GuildId = 9L, Name = "v1" };
        var guild2  = new ReadGuild { GuildId = 9L, Name = "v2" };
        IList<object> guilds = [guild1, guild2];

        var guildResult = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadGuild), guilds);
        guildResult.Count.ShouldBe(1);
        ((ReadGuild)guildResult[0]).Name.ShouldBe("v2");

        // MessageReference: composite PK (MessageId, Ordinal); mixed input
        IList<object> refs =
        [
            new MessageReference { MessageId = 1L, Ordinal = 0 },
            new MessageReference { MessageId = 1L, Ordinal = 0 }, // duplicate → removed
            new MessageReference { MessageId = 1L, Ordinal = 1 }, // different ordinal → kept
        ];

        var refResult = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(MessageReference), refs);
        refResult.Count.ShouldBe(2);
    }

    // -------------------------------------------------------------------------
    // Empty input
    // -------------------------------------------------------------------------

    [Test]
    public void EmptyList_ReturnsEmpty()
    {
        using var db = BuildDb();
        IList<object> input = [];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadChannel), input);

        result.Count.ShouldBe(0);
    }

    // -------------------------------------------------------------------------
    // Order preservation — last occurrence within the batch wins
    // -------------------------------------------------------------------------

    [Test]
    public void ThreeDuplicates_LastValuePreserved()
    {
        using var db = BuildDb();
        IList<object> input =
        [
            new ReadGuild { GuildId = 5L, Name = "first"  },
            new ReadGuild { GuildId = 5L, Name = "second" },
            new ReadGuild { GuildId = 5L, Name = "third"  },
        ];

        var result = EfCoreBulkWriterStatics.DedupeByPrimaryKey(db, typeof(ReadGuild), input);

        result.Count.ShouldBe(1);
        ((ReadGuild)result[0]).Name.ShouldBe("third");
    }
}
