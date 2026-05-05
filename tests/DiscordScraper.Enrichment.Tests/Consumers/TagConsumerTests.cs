using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Enrichment.Consumers;
using DiscordScraper.Enrichment.Ollama;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.Enrichment.Tests.Consumers;

[TestFixture]
public sealed class TagConsumerTests
{
    private IEmbeddingClient _embedding = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    [SetUp]
    public async Task SetUp()
    {
        _embedding = Substitute.For<IEmbeddingClient>();
        _embedding.Model.Returns("nomic-embed-text:v1.5");

        _provider = new ServiceCollection()
            .AddSingleton(_embedding)
            .AddSingleton<IOptions<EnrichmentTagOptions>>(
                Options.Create(new EnrichmentTagOptions()))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<TagConsumer, TagConsumerDefinition>();
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

    private static TagMessageRequest BuildRequest(
        long snowflake = 1L,
        string text = "some message text") => new()
    {
        MessageSnowflake = snowflake,
        PlainText = text,
    };

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Test]
    public async Task Happy_path_calls_EmbedAsync_with_message_plain_text()
    {
        var expectedVector = new float[768];
        expectedVector[0] = 0.5f;
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(expectedVector));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        await client.GetResponse<TagMessageResponse>(BuildRequest(text: "hello world"));

        await _embedding.Received(1).EmbedAsync("hello world", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Happy_path_response_contains_embedding_vector()
    {
        var expectedVector = new float[768];
        expectedVector[3] = 0.42f;
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(expectedVector));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        var response = await client.GetResponse<TagMessageResponse>(BuildRequest());

        response.Message.Embedding.Count.ShouldBe(768);
        response.Message.Embedding[3].ShouldBe(0.42f);
    }

    [Test]
    public async Task Happy_path_response_EmbeddingModelVersion_matches_client_Model()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        var response = await client.GetResponse<TagMessageResponse>(BuildRequest());

        // EmbeddingModelVersion is captured from embedding.Model so re-embed fans can
        // identify stale sagas whose stored model version no longer matches the current model.
        response.Message.EmbeddingModelVersion.ShouldBe("nomic-embed-text:v1.5");
    }

    [Test]
    public async Task Happy_path_response_GeneratedAt_is_not_null()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        var response = await client.GetResponse<TagMessageResponse>(BuildRequest());

        response.Message.GeneratedAt.ShouldNotBeNull();
    }

    // -------------------------------------------------------------------------
    // Null/empty plain text
    // -------------------------------------------------------------------------

    [Test]
    public async Task Null_PlainText_coerces_to_empty_string_before_EmbedAsync()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        await client.GetResponse<TagMessageResponse>(new TagMessageRequest
        {
            MessageSnowflake = 5L,
            PlainText = null!,
        });

        // Consumer coerces null to empty string — EmbedAsync receives "" not null.
        await _embedding.Received(1).EmbedAsync(string.Empty, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Fault propagation — consumer lets HttpRequestException bubble so the
    // TagConsumerDefinition retry policy handles it (saga sees Fault<> only after
    // retry exhaustion).
    // -------------------------------------------------------------------------

    [Test]
    public async Task HttpRequestException_from_EmbedAsync_faults_the_consumer()
    {
        _embedding
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("simulated Ollama unavailable"));

        var client = _harness.GetRequestClient<TagMessageRequest>();

        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<TagMessageResponse>(BuildRequest()));

        var consumerHarness = _harness.GetConsumerHarness<TagConsumer>();
        (await consumerHarness.Consumed.Any<TagMessageRequest>()).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Consumer invocation
    // -------------------------------------------------------------------------

    [Test]
    public async Task Consumer_was_invoked()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        var client = _harness.GetRequestClient<TagMessageRequest>();
        await client.GetResponse<TagMessageResponse>(BuildRequest(snowflake: 99L));

        var consumerHarness = _harness.GetConsumerHarness<TagConsumer>();
        (await consumerHarness.Consumed.Any<TagMessageRequest>()).ShouldBeTrue();
    }
}
