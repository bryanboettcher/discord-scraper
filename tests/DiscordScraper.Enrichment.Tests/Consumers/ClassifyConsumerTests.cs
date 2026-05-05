using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
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
public sealed class ClassifyConsumerTests
{
    private ITaggingClient _tagging = null!;
    private IVectorStore _vectorStore = null!;
    private ISystemClock _clock = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    private static readonly DateTimeOffset FixedNow =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public async Task SetUp()
    {
        _tagging = Substitute.For<ITaggingClient>();
        _tagging.Model.Returns("llama3.1:8b");

        _vectorStore = Substitute.For<IVectorStore>();

        _clock = Substitute.For<ISystemClock>();
        _clock.UtcNow.Returns(FixedNow);

        _provider = new ServiceCollection()
            .AddSingleton(_tagging)
            .AddSingleton(_vectorStore)
            .AddSingleton(_clock)
            .AddSingleton<IOptions<EnrichmentClassifyOptions>>(
                Options.Create(new EnrichmentClassifyOptions()))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ClassifyConsumer, ClassifyConsumerDefinition>();
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

    private static ClassifyMessageRequest BuildRequest(
        long snowflake = 1L,
        long guildId = 10L,
        long channelId = 20L,
        long authorId = 30L,
        string text = "some message text",
        IReadOnlyList<float>? embedding = null) => new()
    {
        MessageSnowflake = snowflake,
        GuildId = guildId,
        ChannelId = channelId,
        AuthorId = authorId,
        CreatedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        PlainText = text,
        Embedding = embedding ?? Enumerable.Repeat(0.1f, 768).ToArray(),
    };

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Test]
    public async Task Happy_path_calls_TagAsync_with_message_plain_text()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet", "postgres"], IsSubstantive: true));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        await client.GetResponse<ClassifyMessageResponse>(BuildRequest(text: "dotnet rocks"));

        await _tagging.Received(1).TagAsync("dotnet rocks", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Happy_path_response_Tags_match_TagResult()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet", "postgres"], IsSubstantive: true));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        var response = await client.GetResponse<ClassifyMessageResponse>(BuildRequest());

        response.Message.Tags.ShouldBe(["dotnet", "postgres"]);
    }

    [Test]
    public async Task Happy_path_response_ClassifyModelVersion_matches_client_Model()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["ai"], IsSubstantive: true));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        var response = await client.GetResponse<ClassifyMessageResponse>(BuildRequest());

        // ClassifyModelVersion is captured from tagging.Model so the ClassificationInvalidated
        // fan-out can identify sagas whose stored version no longer matches.
        response.Message.ClassifyModelVersion.ShouldBe("llama3.1:8b");
    }

    [Test]
    public async Task Happy_path_response_IndexedAt_is_set_to_clock_UtcNow()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["ai"], IsSubstantive: true));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        var response = await client.GetResponse<ClassifyMessageResponse>(BuildRequest());

        response.Message.IndexedAt.ShouldBe(FixedNow);
    }

    [Test]
    public async Task Happy_path_upserts_vector_point_with_correct_MessageId()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet"], IsSubstantive: true));

        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        await client.GetResponse<ClassifyMessageResponse>(BuildRequest(snowflake: 42L));

        captured.ShouldNotBeNull();
        captured![0].MessageId.ShouldBe(42L);
    }

    [Test]
    public async Task Happy_path_upserts_vector_point_with_embedding_from_request()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet"], IsSubstantive: true));

        var embedding = new float[768];
        embedding[7] = 0.99f;

        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        await client.GetResponse<ClassifyMessageResponse>(BuildRequest(embedding: embedding));

        captured.ShouldNotBeNull();
        captured![0].Embedding.Span[7].ShouldBe(0.99f);
    }

    [Test]
    public async Task Happy_path_upserts_vector_point_with_tags_from_TagResult()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["databases", "nosql"], IsSubstantive: true));

        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        await client.GetResponse<ClassifyMessageResponse>(BuildRequest());

        captured.ShouldNotBeNull();
        captured![0].Tags.ShouldBe(["databases", "nosql"]);
    }

    // -------------------------------------------------------------------------
    // Empty tags — TagResult may return empty list for unclassifiable content
    // -------------------------------------------------------------------------

    [Test]
    public async Task Empty_tags_from_TagResult_are_forwarded_to_vector_point()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult([], IsSubstantive: false));

        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        var response = await client.GetResponse<ClassifyMessageResponse>(BuildRequest());

        captured.ShouldNotBeNull();
        captured![0].Tags.ShouldBeEmpty();
        response.Message.Tags.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Fault propagation — no consumer-level retry by design; saga handles via
    // Faulted state + replay. HttpRequestException propagates to the fault handler.
    // -------------------------------------------------------------------------

    [Test]
    public async Task HttpRequestException_from_TagAsync_faults_the_consumer()
    {
        _tagging
            .TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("simulated LLM unavailable"));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();

        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<ClassifyMessageResponse>(BuildRequest()));

        var consumerHarness = _harness.GetConsumerHarness<ClassifyConsumer>();
        (await consumerHarness.Consumed.Any<ClassifyMessageRequest>()).ShouldBeTrue();
    }

    [Test]
    public async Task VectorStore_exception_faults_the_consumer_before_responding()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet"], IsSubstantive: true));
        _vectorStore
            .UpsertManyAsync(Arg.Any<IReadOnlyList<VectorPoint>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated Postgres connection reset"));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();

        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<ClassifyMessageResponse>(BuildRequest()));
    }

    // -------------------------------------------------------------------------
    // Consumer invocation
    // -------------------------------------------------------------------------

    [Test]
    public async Task Consumer_was_invoked()
    {
        _tagging.TagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TagResult(["dotnet"], IsSubstantive: true));

        var client = _harness.GetRequestClient<ClassifyMessageRequest>();
        await client.GetResponse<ClassifyMessageResponse>(BuildRequest(snowflake: 99L));

        var consumerHarness = _harness.GetConsumerHarness<ClassifyConsumer>();
        (await consumerHarness.Consumed.Any<ClassifyMessageRequest>()).ShouldBeTrue();
    }
}
