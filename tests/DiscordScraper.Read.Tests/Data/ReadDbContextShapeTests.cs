using DiscordScraper.Read.Data;
using DiscordScraper.Read.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiscordScraper.Read.Tests.Data;

/// <summary>
/// Verifies the DbContext surface without a live database.
/// Uses an in-memory provider solely to validate model configuration — not for query testing.
/// </summary>
[TestFixture]
public sealed class ReadDbContextShapeTests
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
    public void ReadMessages_DbSetExists()
    {
        _db.ReadMessages.ShouldNotBeNull();
    }

    [Test]
    public void MessageReferences_DbSetExists()
    {
        _db.MessageReferences.ShouldNotBeNull();
    }

    [Test]
    public void MessageAttachments_DbSetExists()
    {
        _db.MessageAttachments.ShouldNotBeNull();
    }

    [Test]
    public void MessageEmbeds_DbSetExists()
    {
        _db.MessageEmbeds.ShouldNotBeNull();
    }

    [Test]
    public void MessageTags_DbSetExists()
    {
        _db.MessageTags.ShouldNotBeNull();
    }

    [Test]
    public void ReadChannels_DbSetExists()
    {
        _db.ReadChannels.ShouldNotBeNull();
    }

    [Test]
    public void ReadGuilds_DbSetExists()
    {
        _db.ReadGuilds.ShouldNotBeNull();
    }

    [Test]
    public void ReadDbContext_HasNoMessageVectorsSet()
    {
        // message_vectors is owned by PgVectorStore; must not appear in ReadDbContext.
        var setNames = _db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .ToList();

        setNames.ShouldNotContain("message_vectors");
    }
}
