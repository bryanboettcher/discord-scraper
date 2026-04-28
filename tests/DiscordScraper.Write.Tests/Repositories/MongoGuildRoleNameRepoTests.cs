using DiscordScraper.Write.Repositories;
using DiscordScraper.Write.Sagas;
using MongoDB.Driver;
using NSubstitute;

namespace DiscordScraper.Write.Tests.Repositories;

[TestFixture]
public sealed class MongoGuildRoleNameRepoTests
{
    // -------------------------------------------------------------------------
    // Empty roleIds → return empty dict, no Mongo round-trip
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetRoleNamesAsync_EmptyRoleIds_ReturnsEmptyWithoutQueryingMongo()
    {
        var db = Substitute.For<IMongoDatabase>();
        var repo = new MongoGuildRoleNameRepo(db);

        var result = await repo.GetRoleNamesAsync(guildId: 1L, roleIds: [], CancellationToken.None);

        result.ShouldBeEmpty();

        // No collection was accessed — the fast-path must short-circuit before any Mongo I/O.
        db.DidNotReceive().GetCollection<GuildSagaState>(Arg.Any<string>());
    }
}
