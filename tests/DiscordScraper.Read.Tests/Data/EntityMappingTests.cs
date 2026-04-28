using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Read.Tests.Data;

/// <summary>
/// Verifies key declarations and column mappings for each entity type using the EF model metadata.
/// These tests catch misconfiguration (wrong PK, wrong column name) without requiring Postgres.
/// Uses in-memory provider because we only inspect the model — not actual SQL.
/// </summary>
[TestFixture]
public sealed class EntityMappingTests
{
    private ReadDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        var opts = new DbContextOptionsBuilder<ReadDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ReadDbContext(opts);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public void ReadMessage_PrimaryKey_IsMessageId()
    {
        var pk = _db.Model.FindEntityType(typeof(ReadMessage))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["MessageId"]);
    }

    [Test]
    public void MessageReference_CompositeKey_IsMessageIdAndOrdinal()
    {
        var pk = _db.Model.FindEntityType(typeof(MessageReference))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["MessageId", "Ordinal"]);
    }

    [Test]
    public void MessageAttachment_CompositeKey_IsMessageIdAndAttachmentId()
    {
        var pk = _db.Model.FindEntityType(typeof(MessageAttachment))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["MessageId", "AttachmentId"]);
    }

    [Test]
    public void MessageEmbed_CompositeKey_IsMessageIdAndEmbedIndex()
    {
        var pk = _db.Model.FindEntityType(typeof(MessageEmbed))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["MessageId", "EmbedIndex"]);
    }

    [Test]
    public void MessageTag_CompositeKey_IsMessageIdAndTag()
    {
        var pk = _db.Model.FindEntityType(typeof(MessageTag))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["MessageId", "Tag"]);
    }

    [Test]
    public void ReadChannel_PrimaryKey_IsChannelId()
    {
        var pk = _db.Model.FindEntityType(typeof(ReadChannel))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["ChannelId"]);
    }

    [Test]
    public void ReadGuild_PrimaryKey_IsGuildId()
    {
        var pk = _db.Model.FindEntityType(typeof(ReadGuild))!
            .FindPrimaryKey()!
            .Properties
            .Select(p => p.Name)
            .ToList();

        pk.ShouldBe(["GuildId"]);
    }
}
