using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
using DiscordScraper.MessageEnhancement.Consumers;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordScraper.MessageEnhancement.Tests.Consumers;

[TestFixture]
public sealed class IndexMessageConsumerTests
{
    private IVectorStore _vectorStore = null!;
    private ISystemClock _clock = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    private static readonly DateTimeOffset FixedNow =
        new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public async Task SetUp()
    {
        _vectorStore = Substitute.For<IVectorStore>();
        _clock = Substitute.For<ISystemClock>();
        _clock.UtcNow.Returns(FixedNow);

        _provider = new ServiceCollection()
            .AddSingleton(_vectorStore)
            .AddSingleton(_clock)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<IndexMessageConsumer, IndexMessageConsumerDefinition>();
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

    private static IndexMessageRequest BuildRequest(
        long snowflake = 100L,
        long guildId = 1L,
        long channelId = 2L,
        long authorId = 3L,
        DateTimeOffset? createdAt = null,
        IReadOnlyList<float>? embedding = null,
        IReadOnlyList<string>? tags = null) => new()
    {
        MessageSnowflake = snowflake,
        GuildId = guildId,
        ChannelId = channelId,
        AuthorId = authorId,
        CreatedAt = createdAt ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Embedding = embedding ?? Enumerable.Repeat(0.1f, 768).ToArray(),
        Tags = tags ?? ["dotnet", "postgres"],
    };

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Test]
    public async Task Happy_path_upserts_one_point_and_responds_with_indexed_at()
    {
        var client = _harness.GetRequestClient<IndexMessageRequest>();
        var response = await client.GetResponse<IndexMessageResponse>(BuildRequest());

        await _vectorStore.Received(1)
            .UpsertManyAsync(Arg.Any<IReadOnlyList<VectorPoint>>(), Arg.Any<CancellationToken>());

        response.Message.IndexedAt.ShouldBe(FixedNow);
    }

    // -------------------------------------------------------------------------
    // VectorPoint field mapping
    // -------------------------------------------------------------------------

    [Test]
    public async Task MessageId_maps_from_request_MessageSnowflake()
    {
        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<IndexMessageRequest>();
        await client.GetResponse<IndexMessageResponse>(BuildRequest(snowflake: 999L));

        captured.ShouldNotBeNull();
        captured![0].MessageId.ShouldBe(999L);
    }

    [Test]
    public async Task Embedding_float_array_is_converted_to_ReadOnlyMemory()
    {
        var expectedVector = new float[768];
        expectedVector[7] = 0.42f;

        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<IndexMessageRequest>();
        await client.GetResponse<IndexMessageResponse>(BuildRequest(embedding: expectedVector));

        captured.ShouldNotBeNull();
        captured![0].Embedding.Span[7].ShouldBe(0.42f);
        captured![0].Embedding.Length.ShouldBe(768);
    }

    // -------------------------------------------------------------------------
    // Edge cases — empty tags and zero vector
    // -------------------------------------------------------------------------

    [Test]
    public async Task Empty_tags_list_still_upserts()
    {
        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<IndexMessageRequest>();
        await client.GetResponse<IndexMessageResponse>(BuildRequest(tags: []));

        captured.ShouldNotBeNull();
        captured![0].Tags.ShouldBeEmpty();
    }

    [Test]
    public async Task Zero_vector_embedding_is_indexed_without_error()
    {
        // EnhanceMessageConsumer may produce a zero vector for attachment-only messages;
        // downstream search handles it. The consumer must not reject it.
        IReadOnlyList<VectorPoint>? captured = null;
        await _vectorStore.UpsertManyAsync(
            Arg.Do<IReadOnlyList<VectorPoint>>(pts => captured = pts),
            Arg.Any<CancellationToken>());

        var client = _harness.GetRequestClient<IndexMessageRequest>();
        var response = await client.GetResponse<IndexMessageResponse>(
            BuildRequest(embedding: new float[768]));

        response.Message.IndexedAt.ShouldBe(FixedNow);
        captured![0].Embedding.Span.SequenceEqual(new float[768]).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // Fault propagation
    // -------------------------------------------------------------------------

    [Test]
    public async Task VectorStore_throws_consumer_faults_and_no_response_is_sent()
    {
        _vectorStore
            .UpsertManyAsync(Arg.Any<IReadOnlyList<VectorPoint>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated Postgres connection reset"));

        var client = _harness.GetRequestClient<IndexMessageRequest>();

        Assert.ThrowsAsync<RequestFaultException>(
            async () => await client.GetResponse<IndexMessageResponse>(BuildRequest()));

        var consumerHarness = _harness.GetConsumerHarness<IndexMessageConsumer>();
        (await consumerHarness.Consumed.Any<IndexMessageRequest>()).ShouldBeTrue();
    }

    // -------------------------------------------------------------------------
    // CancellationToken propagation
    // -------------------------------------------------------------------------

    [Test]
    public async Task CancellationToken_is_threaded_through_to_UpsertManyAsync()
    {
        CancellationToken captured = default;
        await _vectorStore.UpsertManyAsync(
            Arg.Any<IReadOnlyList<VectorPoint>>(),
            Arg.Do<CancellationToken>(ct => captured = ct));

        var client = _harness.GetRequestClient<IndexMessageRequest>();
        await client.GetResponse<IndexMessageResponse>(BuildRequest());

        // MT's ConsumeContext wires a real linked token — it is not CancellationToken.None.
        captured.ShouldNotBe(CancellationToken.None);
    }
}
