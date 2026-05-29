using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Events.Message;
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

    private static TagMessageRequested BuildRequest(
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

        await _harness.Bus.Publish(BuildRequest(text: "hello world"));

        await _harness.GetConsumerHarness<TagConsumer>()
            .Consumed.Any<TagMessageRequested>(x => x.Context.Message.PlainText == "hello world");

        await _embedding.Received(1).EmbedAsync("hello world", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Happy_path_publishes_MessageTagged_with_embedding_vector()
    {
        var expectedVector = new float[768];
        expectedVector[3] = 0.42f;
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(expectedVector));

        await _harness.Bus.Publish(BuildRequest());

        var published = await _harness.Published.SelectAsync<MessageTagged>().FirstOrDefaultAsync();
        published.ShouldNotBeNull();
        published.Context.Message.Embedding.Length.ShouldBe(768);
        published.Context.Message.Embedding[3].ShouldBe(0.42f);
    }

    [Test]
    public async Task Happy_path_MessageTagged_EmbeddingModelVersion_matches_client_Model()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        await _harness.Bus.Publish(BuildRequest());

        var published = await _harness.Published.SelectAsync<MessageTagged>().FirstOrDefaultAsync();
        published.ShouldNotBeNull();
        // EmbeddingModelVersion is captured from embedding.Model so re-embed fans can
        // identify stale sagas whose stored model version no longer matches the current model.
        published.Context.Message.EmbeddingModelVersion.ShouldBe("nomic-embed-text:v1.5");
    }

    // -------------------------------------------------------------------------
    // Null/empty plain text
    // -------------------------------------------------------------------------

    [Test]
    public async Task Null_PlainText_coerces_to_empty_string_before_EmbedAsync()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        await _harness.Bus.Publish(new TagMessageRequested
        {
            MessageSnowflake = 5L,
            PlainText = null!,
        });

        await _harness.GetConsumerHarness<TagConsumer>()
            .Consumed.Any<TagMessageRequested>();

        // Consumer coerces null to empty string — EmbedAsync receives "" not null.
        await _embedding.Received(1).EmbedAsync(string.Empty, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Fault propagation — consumer lets HttpRequestException bubble so MT
    // auto-publishes Fault<TagMessageRequested>. TagConsumerDefinition retry
    // policy handles transient failures; saga sees Fault<> only after exhaustion.
    // -------------------------------------------------------------------------

    [Test]
    public async Task HttpRequestException_from_EmbedAsync_faults_the_consumer()
    {
        _embedding
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("simulated Ollama unavailable"));

        await _harness.Bus.Publish(BuildRequest());

        var consumerHarness = _harness.GetConsumerHarness<TagConsumer>();
        (await consumerHarness.Consumed.Any<TagMessageRequested>()).ShouldBeTrue();

        // No MessageTagged published — consumer faulted before completing.
        (await _harness.Published.Any<MessageTagged>()).ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Consumer invocation
    // -------------------------------------------------------------------------

    [Test]
    public async Task Consumer_was_invoked()
    {
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new float[768]));

        await _harness.Bus.Publish(BuildRequest(snowflake: 99L));

        var consumerHarness = _harness.GetConsumerHarness<TagConsumer>();
        (await consumerHarness.Consumed.Any<TagMessageRequested>()).ShouldBeTrue();
    }
}
