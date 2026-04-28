using System.Net;
using System.Net.Http.Json;
using DiscordScraper.Api.Admin;
using DiscordScraper.Contracts.Events.Channel;
using DiscordScraper.Contracts.Events.Guild;
using MassTransit;
using NSubstitute;

namespace DiscordScraper.Api.Tests;

[TestFixture]
public abstract class AdminEndpointsTests
{
    protected ApiTestFactory Factory = null!;
    protected HttpClient Client = null!;

    private static readonly DateTimeOffset FixedNow =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly SagaCountsByState DefaultCounts = new(
        Guild:   new Dictionary<string, long> { ["Synced"] = 2 },
        Channel: new Dictionary<string, long> { ["CaughtUp"] = 10, ["Syncing"] = 3 },
        Message: new Dictionary<string, long> { ["Indexed"] = 100 });

    private static readonly ReadStoreCounts DefaultReadStore = new(
        Messages: 100, Channels: 10, Guilds: 2, Vectors: 90);

    private static GuildSagaSnapshot BuildGuildSnapshot(long id = 1L) => new(
        GuildId: id, Name: "TestGuild", CurrentState: "Synced",
        LastUpdatedAt: FixedNow, LastSyncedAt: FixedNow,
        LastSyncChannelCount: 5, RoleCount: 3);

    private static ChannelSagaSnapshot BuildChannelSnapshot(long id = 10L, long guildId = 1L) => new(
        ChannelId: id, GuildId: guildId, Name: "general", ChannelType: 0,
        CurrentState: "CaughtUp", LastSyncedSnowflake: 999L, LastSyncedAt: FixedNow,
        IsCaughtUpAtLastPoll: true, LastSyncMessageCount: 50, PinSetCanonical: null);

    [SetUp]
    public void BaseSetUp()
    {
        Factory = new ApiTestFactory();
        Factory.SystemClock.UtcNow.Returns(FixedNow);
        Client = Factory.CreateClient();
        Arrange();
    }

    [TearDown]
    public void BaseTearDown()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    protected virtual void Arrange() { }

    // -------------------------------------------------------------------------

    public sealed class Stats_returns_aggregated_counts : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection.GetCountsAsync(Arg.Any<CancellationToken>())
                .Returns(DefaultCounts);
            Factory.ReadStoreStatistics.GetCountsAsync(Arg.Any<CancellationToken>())
                .Returns(DefaultReadStore);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/stats");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_aggregated_data()
        {
            var response = await Client.GetAsync("/api/admin/stats");
            var body = await response.Content.ReadFromJsonAsync<AdminStatsResponse>();
            body.ShouldNotBeNull();
            body.ReadStore.Messages.ShouldBe(100L);
            body.Sagas.Guild["Synced"].ShouldBe(2L);
            body.GeneratedAt.ShouldBe(FixedNow);
        }
    }

    public sealed class SyncStatus_returns_counts_and_syncing_channels : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection.GetCountsAsync(Arg.Any<CancellationToken>())
                .Returns(DefaultCounts);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/sync/status");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_include_syncing_channel_count()
        {
            var response = await Client.GetAsync("/api/admin/sync/status");
            var body = await response.Content.ReadFromJsonAsync<SyncStatusResponse>();
            body.ShouldNotBeNull();
            // "Syncing" key in channel counts = 3
            body.RecentlySyncedChannels.ShouldBe(3);
            body.GeneratedAt.ShouldBe(FixedNow);
        }
    }

    public sealed class ListGuilds_returns_all_guilds : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection.ListGuildSagasAsync(Arg.Any<CancellationToken>())
                .Returns(new[] { BuildGuildSnapshot(1L), BuildGuildSnapshot(2L) });
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/sync/guilds");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_guild_list()
        {
            var response = await Client.GetAsync("/api/admin/sync/guilds");
            var body = await response.Content.ReadFromJsonAsync<List<GuildSagaSnapshot>>();
            body.ShouldNotBeNull();
            body.Count.ShouldBe(2);
        }
    }

    public sealed class ListChannels_with_no_filter_returns_all : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection
                .ListChannelSagasAsync(null, Arg.Any<CancellationToken>())
                .Returns(new[] { BuildChannelSnapshot(10L, 1L), BuildChannelSnapshot(11L, 1L) });
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/sync/channels");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_all_channels()
        {
            var response = await Client.GetAsync("/api/admin/sync/channels");
            var body = await response.Content.ReadFromJsonAsync<List<ChannelSagaSnapshot>>();
            body.ShouldNotBeNull();
            body.Count.ShouldBe(2);
        }
    }

    public sealed class ListChannels_with_guildId_filter_passes_filter : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection
                .ListChannelSagasAsync(123L, Arg.Any<CancellationToken>())
                .Returns(new[] { BuildChannelSnapshot(10L, 123L) });
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/sync/channels?guildId=123");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_forward_guildId_to_service()
        {
            var response = await Client.GetAsync("/api/admin/sync/channels?guildId=123");
            var body = await response.Content.ReadFromJsonAsync<List<ChannelSagaSnapshot>>();
            body.ShouldNotBeNull();
            body.Count.ShouldBe(1);
            body[0].GuildId.ShouldBe(123L);
        }
    }

    public sealed class ForceGuildSync_publishes_and_returns_202 : AdminEndpointsTests
    {
        [Test]
        public async Task It_should_return_202()
        {
            var response = await Client.PostAsync("/api/admin/sync/guilds/42", null);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        [Test]
        public async Task It_should_publish_GuildSyncRequested_with_correct_guildId()
        {
            await Client.PostAsync("/api/admin/sync/guilds/42", null);

            await Factory.PublishEndpoint.Received(1)
                .Publish<GuildSyncRequested>(
                    Arg.Is<object>(o => HasProperty(o, "GuildId", 42L)),
                    Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task It_should_stamp_clock_UtcNow_on_publish()
        {
            await Client.PostAsync("/api/admin/sync/guilds/42", null);

            await Factory.PublishEndpoint.Received(1)
                .Publish<GuildSyncRequested>(
                    Arg.Is<object>(o => HasProperty(o, "LastUpdatedAt", FixedNow)),
                    Arg.Any<CancellationToken>());
        }

        private static bool HasProperty<TValue>(object msg, string name, TValue expected)
        {
            var prop = msg.GetType().GetProperty(name);
            return prop is not null && EqualityComparer<TValue>.Default.Equals((TValue)prop.GetValue(msg)!, expected);
        }
    }

    public sealed class ForceChannelSync_with_no_guildId_returns_400 : AdminEndpointsTests
    {
        [Test]
        public async Task It_should_return_400()
        {
            var response = await Client.PostAsync("/api/admin/sync/channels/99", null);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    public sealed class ForceChannelSync_publishes_and_returns_202 : AdminEndpointsTests
    {
        [Test]
        public async Task It_should_return_202()
        {
            var response = await Client.PostAsync("/api/admin/sync/channels/99?guildId=1", null);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        [Test]
        public async Task It_should_publish_ChannelSyncRequested_with_correct_ids()
        {
            await Client.PostAsync("/api/admin/sync/channels/99?guildId=1", null);

            await Factory.PublishEndpoint.Received(1)
                .Publish<ChannelSyncRequested>(
                    Arg.Is<object>(o => HasProperty(o, "ChannelId", 99L) && HasProperty(o, "GuildId", 1L)),
                    Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task It_should_pass_through_cursorSnowflake()
        {
            await Client.PostAsync("/api/admin/sync/channels/99?guildId=1&cursorSnowflake=555", null);

            await Factory.PublishEndpoint.Received(1)
                .Publish<ChannelSyncRequested>(
                    Arg.Is<object>(o => HasNullableLongProperty(o, "CursorSnowflake", 555L)),
                    Arg.Any<CancellationToken>());
        }

        private static bool HasProperty<TValue>(object msg, string name, TValue expected)
        {
            var prop = msg.GetType().GetProperty(name);
            return prop is not null && EqualityComparer<TValue>.Default.Equals((TValue)prop.GetValue(msg)!, expected);
        }

        private static bool HasNullableLongProperty(object msg, string name, long expected)
        {
            var prop = msg.GetType().GetProperty(name);
            return prop is not null && prop.GetValue(msg) is long val && val == expected;
        }
    }

    public sealed class GetMessageSaga_when_saga_exists : AdminEndpointsTests
    {
        private static readonly MessageSagaSnapshot Snapshot = new(
            MessageSnowflake: 12345L, ChannelId: 10L, GuildId: 1L, AuthorId: 99L,
            AuthorIsBot: false, CurrentState: "Indexed", LastUpdatedAt: FixedNow,
            MessageCreatedAt: FixedNow, EditedTimestamp: null, HasPendingEdit: false,
            IsSubstantive: true, IsBot: false, DetectedLanguage: "en",
            Tags: ["tech"], IndexedAt: FixedNow);

        protected override void Arrange()
        {
            Factory.SagaIntrospection
                .GetMessageSagaAsync(12345L, Arg.Any<CancellationToken>())
                .Returns(Snapshot);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/admin/sagas/messages/12345");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_the_snapshot()
        {
            var response = await Client.GetAsync("/api/admin/sagas/messages/12345");
            var body = await response.Content.ReadFromJsonAsync<MessageSagaSnapshot>();
            body.ShouldNotBeNull();
            body.MessageSnowflake.ShouldBe(12345L);
            body.CurrentState.ShouldBe("Indexed");
        }
    }

    public sealed class GetMessageSaga_when_saga_does_not_exist : AdminEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.SagaIntrospection
                .GetMessageSagaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns((MessageSagaSnapshot?)null);
        }

        [Test]
        public async Task It_should_return_404()
        {
            var response = await Client.GetAsync("/api/admin/sagas/messages/99999");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
