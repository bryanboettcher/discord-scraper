using System.Net;
using System.Net.Http.Json;
using DiscordScraper.Api.Endpoints.Models;
using DiscordScraper.Core.Queries;
using NSubstitute;

namespace DiscordScraper.Api.Tests;

[TestFixture]
public abstract class MessageEndpointsTests
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

    private static RenderedMessage BuildMessage(long id = 1L) => new(
        MessageId: id,
        ChannelId: 100L,
        GuildId: 200L,
        AuthorId: 300L,
        CreatedAt: DateTimeOffset.UtcNow,
        EditedAt: null,
        Body: "hello world",
        Tags: [],
        AuthorName: "testuser",
        ChannelName: "general");

    public sealed class When_searching_messages : MessageEndpointsTests
    {
        private readonly ScoredMessage _scored = new(
            Message: new RenderedMessage(1L, 100L, 200L, 300L, DateTimeOffset.UtcNow, null,
                "test", [], "user", "channel"),
            SemanticScore: 0f,
            LexicalScore: 0.9f,
            CombinedScore: 0.9f,
            Snippet: "test snippet");

        protected override void Arrange()
        {
            Factory.MessageQueryService
                .SearchAsync(Arg.Any<MessageSearchQuery>(), Arg.Any<CancellationToken>())
                .Returns([_scored]);
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.PostAsJsonAsync("/api/messages/search",
                new MessageSearchRequest("test"));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_results()
        {
            var response = await Client.PostAsJsonAsync("/api/messages/search",
                new MessageSearchRequest("test"));
            var results = await response.Content.ReadFromJsonAsync<List<ScoredMessage>>();
            results.ShouldNotBeNull();
            results.Count.ShouldBe(1);
        }
    }

    public sealed class When_getting_message_that_exists : MessageEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.MessageQueryService
                .GetMessageAsync(42L, Arg.Any<RenderFormat>(), Arg.Any<CancellationToken>())
                .Returns(BuildMessage(42L));
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/messages/42");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Test]
        public async Task It_should_return_the_message()
        {
            var response = await Client.GetAsync("/api/messages/42");
            var message = await response.Content.ReadFromJsonAsync<RenderedMessage>();
            message.ShouldNotBeNull();
            message.MessageId.ShouldBe(42L);
        }
    }

    public sealed class When_getting_message_that_does_not_exist : MessageEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.MessageQueryService
                .GetMessageAsync(Arg.Any<long>(), Arg.Any<RenderFormat>(), Arg.Any<CancellationToken>())
                .Returns((RenderedMessage?)null);
        }

        [Test]
        public async Task It_should_return_404()
        {
            var response = await Client.GetAsync("/api/messages/999");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    public sealed class When_getting_conversation_context_that_exists : MessageEndpointsTests
    {
        protected override void Arrange()
        {
            var center = BuildMessage(10L);
            Factory.MessageQueryService
                .GetConversationContextAsync(10L, Arg.Any<int>(), Arg.Any<RenderFormat>(), Arg.Any<CancellationToken>())
                .Returns(new RenderedConversation(center, [], []));
        }

        [Test]
        public async Task It_should_return_200()
        {
            var response = await Client.GetAsync("/api/messages/10/context?radius=3");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    public sealed class When_getting_conversation_context_with_null_center : MessageEndpointsTests
    {
        protected override void Arrange()
        {
            Factory.MessageQueryService
                .GetConversationContextAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<RenderFormat>(), Arg.Any<CancellationToken>())
                .Returns(new RenderedConversation(null, [], []));
        }

        [Test]
        public async Task It_should_return_404()
        {
            var response = await Client.GetAsync("/api/messages/999/context?radius=3");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
