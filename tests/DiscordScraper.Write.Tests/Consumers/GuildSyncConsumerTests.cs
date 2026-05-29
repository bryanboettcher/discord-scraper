using DiscordScraper.Contracts;
using DiscordScraper.Contracts.Events.Channel;
using Microsoft.Extensions.Time.Testing;
using DiscordScraper.Contracts.Events.Guild;
using DiscordScraper.Discord;
using DiscordScraper.Discord.Models;
using DiscordScraper.Write.Consumers;
using DiscordScraper.Write.Repositories;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.Write.Tests.Consumers;

[TestFixture]
public sealed class GuildSyncConsumerTests
{
    private const long GuildId = 42L;

    private static readonly DiscordGuildRaw TestGuild = new(GuildId, "Test Guild", "{}");

    // Payload with 3 named roles + the @everyone role (id == GuildId). @everyone must be filtered out.
    private static readonly string RolePayload = $$"""
        {
            "id": "{{GuildId}}",
            "name": "Test Guild",
            "roles": [
                { "id": "{{GuildId}}", "name": "@everyone" },
                { "id": "1001", "name": "Admin" },
                { "id": "1002", "name": "Moderator" },
                { "id": "1003", "name": "Member" }
            ]
        }
        """;

    private static readonly DiscordGuildRaw TestGuildWithRoles = new(GuildId, "Test Guild", RolePayload);

    private static DiscordChannelRaw MakeChannel(long id, int type = 0) =>
        new(id, GuildId, type, null, $"channel-{id}", "{}");

    // ---------------------------------------------------------------------------
    // Cursor repo returns empty → all channels get cursor=0
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoEmpty_AllChannelsGetZeroCursor()
    {
        var channel1 = MakeChannel(1001L);
        var channel2 = MakeChannel(1002L);

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1, channel2]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        // Wait for both ChannelSyncDue publishes before asserting counts.
        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue("GuildChanged signals consumer completed");

        var published = harness.Published.Select<ChannelSyncDue>().ToList();
        published.Count.ShouldBe(2);
        published.ShouldAllBe(p => p.Context.Message.CursorSnowflake == 0L,
            "All channels should get cursor=0 when repo returns empty");
    }

    // ---------------------------------------------------------------------------
    // Cursor repo returns subset → matched channels get their cursor, others get 0
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoPartialMatch_MatchedChannelsGetCursorOthersGetZero()
    {
        var channel1 = MakeChannel(2001L);
        var channel2 = MakeChannel(2002L);
        var channel3 = MakeChannel(2003L);

        const long cursor1 = 999_000_000_000L;

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1, channel2, channel3]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(
                new Dictionary<long, long> { [2001L] = cursor1 })); // only channel1 has a cursor

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        // GuildChanged is the terminal publish — wait for it so all ChannelSyncDue are queued.
        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var byChannel = harness.Published.Select<ChannelSyncDue>()
            .ToDictionary(p => p.Context.Message.ChannelId, p => p.Context.Message.CursorSnowflake);

        byChannel[2001L].ShouldBe(cursor1);
        byChannel[2002L].ShouldBe(0L);
        byChannel[2003L].ShouldBe(0L);
    }

    // ---------------------------------------------------------------------------
    // Cursor repo throws → consumer faults, exception propagated as Fault message
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_CursorRepoThrows_ConsumerFaultPublished()
    {
        var channel1 = MakeChannel(3001L);

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([channel1]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Mongo unavailable"));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        // Wait for the fault to be published (consumer threw)
        (await harness.Published.Any<Fault<GuildSyncDue>>()).ShouldBeTrue(
            "A fault should be published when the cursor repo throws");

        // No channel sync requests published — the exception aborted the publish loop
        harness.Published.Select<ChannelSyncDue>().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Roles extraction: 3 named roles + @everyone in payload → GuildChanged carries 3, no @everyone
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_GuildPayloadWithRoles_GuildChangedCarriesRolesMinusEveryone()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuildWithRoles);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var guildChanged = harness.Published.Select<GuildChanged>().Single().Context.Message;

        guildChanged.Roles.Count.ShouldBe(3, "@everyone should be filtered out, leaving 3 roles");

        var roleIds = guildChanged.Roles.Select(r => r.Id).ToHashSet();
        roleIds.ShouldContain(1001L);
        roleIds.ShouldContain(1002L);
        roleIds.ShouldContain(1003L);
        roleIds.ShouldNotContain(GuildId, "@everyone role id equals GuildId and must be excluded");

        var names = guildChanged.Roles.ToDictionary(r => r.Id, r => r.Name);
        names[1001L].ShouldBe("Admin");
        names[1002L].ShouldBe("Moderator");
        names[1003L].ShouldBe("Member");
    }

    // ---------------------------------------------------------------------------
    // Empty payload (no roles field) → GuildChanged carries empty list, no fault
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_GuildPayloadWithNoRolesField_GuildChangedCarriesEmptyRoles()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuild);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var guildChanged = harness.Published.Select<GuildChanged>().Single().Context.Message;
        guildChanged.Roles.ShouldBeEmpty("empty payload has no roles array");
    }

    // ---------------------------------------------------------------------------
    // ChannelChanged published with ChannelType + ParentId from Discord model
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_PublishesChannelChanged_WithTypeAndParentId()
    {
        // Thread (type=11) with a parent channel
        var thread = new DiscordChannelRaw(5001L, GuildId, 11, 5000L, "my-thread", "{}");
        var textChannel = MakeChannel(5000L, type: 0);

        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(TestGuildWithRoles);
        discord.GetGuildChannelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([textChannel]);
        discord.GetGuildActiveThreadsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([thread]);

        var cursorRepo = Substitute.For<IChannelCursorRepo>();
        cursorRepo.GetCursorsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, long>>(new Dictionary<long, long>()));

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue("GuildChanged signals consumer completed");

        var channelChangedByChannel = harness.Published.Select<ChannelChanged>()
            .Select(p => p.Context.Message)
            .ToDictionary(m => m.ChannelId);

        channelChangedByChannel.Count.ShouldBe(2, "One ChannelChanged per discovered channel");

        var textMsg = channelChangedByChannel[5000L];
        textMsg.ChannelType.ShouldBe(0);
        textMsg.ParentId.ShouldBeNull();

        var threadMsg = channelChangedByChannel[5001L];
        threadMsg.ChannelType.ShouldBe(11);
        threadMsg.ParentId.ShouldBe(5000L);
    }

    // ---------------------------------------------------------------------------
    // Discord 403 on guild fetch → GuildChanged(IsPresent=false); no channel events; no fault
    // ---------------------------------------------------------------------------

    [Test]
    public async Task Consume_Discord403OnGuildFetch_PublishesGuildChangedNotPresent_NoChannelEvents()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Net.Http.HttpRequestException(
                "403 Forbidden", inner: null, statusCode: System.Net.HttpStatusCode.Forbidden));

        var cursorRepo = Substitute.For<IChannelCursorRepo>();

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var guildChanged = harness.Published.Select<GuildChanged>().Single().Context.Message;
        guildChanged.IsPresent.ShouldBeFalse();

        // No per-channel events published
        harness.Published.Select<ChannelSyncDue>().ShouldBeEmpty();
        harness.Published.Select<ChannelChanged>().ShouldBeEmpty();

        // No fault
        harness.Published.Select<Fault<GuildSyncDue>>().ShouldBeEmpty();
    }

    [Test]
    public async Task Consume_Discord404OnGuildFetch_PublishesGuildChangedNotPresent_NoChannelEvents()
    {
        var discord = Substitute.For<IDiscordClient>();
        discord.GetGuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.Net.Http.HttpRequestException(
                "404 Not Found", inner: null, statusCode: System.Net.HttpStatusCode.NotFound));

        var cursorRepo = Substitute.For<IChannelCursorRepo>();

        await using var provider = BuildProvider(discord, cursorRepo);
        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish<GuildSyncDue>(new
        {
            CorrelationId = DeterministicGuid.FromSnowflake(GuildId),
            GuildId = GuildId, CurrentState = "Requested", UpdatedOn = DateTimeOffset.UtcNow,
        });

        (await harness.Published.Any<GuildChanged>()).ShouldBeTrue();

        var guildChanged = harness.Published.Select<GuildChanged>().Single().Context.Message;
        guildChanged.IsPresent.ShouldBeFalse();
        harness.Published.Select<ChannelSyncDue>().ShouldBeEmpty();
        harness.Published.Select<Fault<GuildSyncDue>>().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(IDiscordClient discord, IChannelCursorRepo cursorRepo)
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(DateTimeOffset.UtcNow);

        return new ServiceCollection()
            .AddSingleton(discord)
            .AddSingleton(cursorRepo)
            .AddSingleton<TimeProvider>(clock)
            .AddMassTransitTestHarness(cfg =>
            {
                // Omit GuildSyncConsumerDefinition: it configures UseMongoDbOutbox which
                // requires a live Mongo instance not available in the unit test harness.
                cfg.AddConsumer<GuildSyncConsumer>();
            })
            .BuildServiceProvider(true);
    }
}
