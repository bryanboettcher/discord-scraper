using System.Net;
using System.Net.Http.Json;
using DiscordScraper.Core.Queries;
using NSubstitute;

namespace DiscordScraper.Api.Tests;

[TestFixture]
public abstract class ChannelEndpointsTests
{
    protected ApiTestFactory Factory = null!;
    protected HttpClient Client = null!;

    [SetUp]
    public void BaseSetUp()
    {
        Factory = new ApiTestFactory();
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

    public sealed class When_getting_active_channels : ChannelEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.ChannelQueryService
                .GetActiveChannelsAsync(500L, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([new ChannelSummary(101L, "general", 42, DateTimeOffset.UtcNow)]);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/guilds/500/channels/active?sinceHours=24");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_channel_summaries()
        {
            var response = await Client.GetAsync("/api/guilds/500/channels/active?sinceHours=24");
            var channels = await response.Content.ReadFromJsonAsync<List<ChannelSummary>>();
            channels.ShouldNotBeNull();
            channels.Count.ShouldBe(1);
            channels[0].ChannelId.ShouldBe(101L);
        }
    }

    public sealed class When_getting_recent_messages : ChannelEndpointsTests
    {
        protected override void Arrange()
        {
            var msg = new RenderedMessage(1L, 200L, 300L, 400L, DateTimeOffset.UtcNow, null,
                "hello", [], "user", "general");
            Factory.ChannelQueryService
                .GetRecentMessagesAsync(200L, Arg.Any<int>(), Arg.Any<RenderFormat>(), Arg.Any<CancellationToken>())
                .Returns([msg]);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/channels/200/messages/recent?count=50");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_messages()
        {
            var response = await Client.GetAsync("/api/channels/200/messages/recent?count=50");
            var messages = await response.Content.ReadFromJsonAsync<List<RenderedMessage>>();
            messages.ShouldNotBeNull();
            messages.Count.ShouldBe(1);
        }
    }
}
