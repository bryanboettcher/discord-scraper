using DiscordScraper.Contracts.IR;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.MessageEnhancement.Consumers;
using DiscordScraper.MessageEnhancement.Ollama;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.MessageEnhancement.Tests.Consumers;

[TestFixture]
public sealed class EnhanceMessageConsumerTests
{
    private IOllamaTaggingClient _tagging = null!;
    private IOllamaEmbeddingClient _embedding = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    [SetUp]
    public async Task SetUp()
    {
        _tagging = Substitute.For<IOllamaTaggingClient>();
        _embedding = Substitute.For<IOllamaEmbeddingClient>();

        _provider = new ServiceCollection()
            .AddSingleton(_tagging)
            .AddSingleton(_embedding)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<EnhanceMessageConsumer, EnhanceMessageConsumerDefinition>();
            })
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private static EnhanceMessageRequest BuildRequest(string plainText = "Is Postgres better than MySQL for time-series?") =>
        new()
        {
            MessageSnowflake = 100L,
            IR = new MessageIR([], [], [], null, DateTimeOffset.UtcNow),
            PlainText = plainText,
        };

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Test]
    public async Task Happy_path_response_carries_tags_and_embedding()
    {
        var expectedTags = new[] { "python", "async" };
        var expectedVector = new float[768];
        expectedVector[0] = 0.5f;

        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(expectedTags, IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(expectedVector));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        var response = await client.GetResponse<EnhanceMessageResponse>(BuildRequest());

        response.Message.Tags.ShouldBe(expectedTags);
        response.Message.Embedding.Count.ShouldBe(768);
        response.Message.Embedding[0].ShouldBe(0.5f);
    }

    [Test]
    public async Task Happy_path_both_clients_called_exactly_once()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["postgres"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        await client.GetResponse<EnhanceMessageResponse>(BuildRequest());

        await _tagging.Received(1).TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _embedding.Received(1).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // PlainText threading
    // -------------------------------------------------------------------------

    [Test]
    public async Task PlainText_is_passed_to_both_clients()
    {
        const string text = "How do I configure pgvector HNSW index parameters?";

        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["pgvector"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        await client.GetResponse<EnhanceMessageResponse>(BuildRequest(text));

        await _tagging.Received(1).TagAsync(text, Arg.Any<CancellationToken>());
        await _embedding.Received(1).EmbedAsync(text, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Empty plain text — graceful
    // -------------------------------------------------------------------------

    [Test]
    public async Task Empty_PlainText_passes_empty_string_to_clients_and_responds()
    {
        _tagging.TagAsync(string.Empty, Arg.Any<CancellationToken>())
            .Returns(new TagResult([], IsSubstantive: false));
        _embedding.EmbedAsync(string.Empty, Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        var response = await client.GetResponse<EnhanceMessageResponse>(BuildRequest(string.Empty));

        // Consumer responds — no exception, empty tags, zero vector
        response.Message.Tags.Count.ShouldBe(0);
        response.Message.Embedding.Count.ShouldBe(768);
    }

    // -------------------------------------------------------------------------
    // Fault propagation
    // -------------------------------------------------------------------------

    [Test]
    public async Task Tagging_throws_consumer_faults_and_response_is_not_sent()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Ollama unreachable"));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();

        // MT wraps faulted consumers into a RequestFaultException at the client side
        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<EnhanceMessageResponse>(BuildRequest()));

        var consumerHarness = _harness.GetConsumerHarness<EnhanceMessageConsumer>();
        (await consumerHarness.Consumed.Any<EnhanceMessageRequest>()).ShouldBeTrue();
    }

    [Test]
    public async Task Embedding_throws_consumer_faults_and_response_is_not_sent()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["docker"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Ollama returned no embedding vector"));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();

        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<EnhanceMessageResponse>(BuildRequest()));

        var consumerHarness = _harness.GetConsumerHarness<EnhanceMessageConsumer>();
        (await consumerHarness.Consumed.Any<EnhanceMessageRequest>()).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Cancellation token propagation
    // -------------------------------------------------------------------------

    [Test]
    public async Task CancellationToken_is_threaded_through_to_both_clients()
    {
        // Capture the tokens actually passed by the consumer — both must be non-default
        // (MT's ConsumeContext provides a real token wired to the bus).
        CancellationToken capturedTagCt = default;
        CancellationToken capturedEmbedCt = default;

        _tagging.TagAsync(Arg.Any<string>(), Arg.Do<CancellationToken>(ct => capturedTagCt = ct))
            .Returns(new TagResult(["test"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Do<CancellationToken>(ct => capturedEmbedCt = ct))
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        await client.GetResponse<EnhanceMessageResponse>(BuildRequest());

        // Both tokens must be something MT provided — they are not CancellationToken.None
        // because the harness wires a real linked token per message.
        capturedTagCt.ShouldNotBe(CancellationToken.None);
        capturedEmbedCt.ShouldNotBe(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Model version stamping — required for re-enrichment fan-out
    // -------------------------------------------------------------------------

    [Test]
    public async Task Response_carries_embedding_model_version_from_client()
    {
        _embedding.Model.Returns("mxbai-embed-large");
        _tagging.Model.Returns("qwen2.5-coder:7b");

        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["test"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        var response = await client.GetResponse<EnhanceMessageResponse>(BuildRequest());

        response.Message.EmbeddingModelVersion.ShouldBe("mxbai-embed-large");
    }

    [Test]
    public async Task Response_carries_tag_model_version_from_client()
    {
        _embedding.Model.Returns("nomic-embed-text");
        _tagging.Model.Returns("llama3.1:70b");

        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["test"], IsSubstantive: true));
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<EnhanceMessageRequest>();
        var response = await client.GetResponse<EnhanceMessageResponse>(BuildRequest());

        response.Message.TagModelVersion.ShouldBe("llama3.1:70b");
    }
}
