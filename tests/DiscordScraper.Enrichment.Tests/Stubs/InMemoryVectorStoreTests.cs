using DiscordScraper.Core.Vector;
using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class InMemoryVectorStoreTests
{
    private InMemoryVectorStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryVectorStore();
    }

    [TearDown]
    public void Teardown()
    {
        _store.Clear();
    }

    [Test]
    public async Task UpsertManyAsync_InsertsVectors()
    {
        var point = new VectorPoint(
            MessageId: 123,
            Embedding: new float[] { 0.1f, 0.2f, 0.3f },
            ChannelId: 456,
            GuildId: 789,
            AuthorId: 999,
            CreatedAt: DateTimeOffset.UtcNow,
            Tags: new[] { "tag1", "tag2" });

        await _store.UpsertManyAsync(new[] { point }, CancellationToken.None);
        Assert.That(_store.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task UpsertManyAsync_UpdatesExistingVector()
    {
        var point1 = new VectorPoint(
            MessageId: 123,
            Embedding: new float[] { 0.1f, 0.2f },
            ChannelId: 456,
            GuildId: 789,
            AuthorId: 999,
            CreatedAt: DateTimeOffset.UtcNow,
            Tags: new[] { "tag1" });

        var point2 = new VectorPoint(
            MessageId: 123,
            Embedding: new float[] { 0.3f, 0.4f },
            ChannelId: 456,
            GuildId: 789,
            AuthorId: 999,
            CreatedAt: DateTimeOffset.UtcNow,
            Tags: new[] { "tag2" });

        await _store.UpsertManyAsync(new[] { point1 }, CancellationToken.None);
        await _store.UpsertManyAsync(new[] { point2 }, CancellationToken.None);
        Assert.That(_store.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SearchAsync_FindsVectorsWithinDistance()
    {
        var point = new VectorPoint(
            MessageId: 123,
            Embedding: new float[] { 1f, 0f, 0f },
            ChannelId: 456,
            GuildId: 789,
            AuthorId: 999,
            CreatedAt: DateTimeOffset.UtcNow,
            Tags: new[] { "tag1" });

        await _store.UpsertManyAsync(new[] { point }, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f, 0f });
        var results = await _store.SearchAsync(query, new VectorFilter(), topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(123));
        Assert.That(results[0].Score, Is.EqualTo(1f));
    }

    [Test]
    public async Task SearchAsync_RanksByScore()
    {
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "a" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "b" }),
            new VectorPoint(125, new float[] { 0.5f, 0.5f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "c" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var results = await _store.SearchAsync(query, new VectorFilter(), topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(3));
        Assert.That(results[0].Score, Is.GreaterThanOrEqualTo(results[1].Score));
        Assert.That(results[1].Score, Is.GreaterThanOrEqualTo(results[2].Score));
    }

    [Test]
    public async Task SearchAsync_TopK_LimitsResults()
    {
        var points = Enumerable.Range(1, 10)
            .Select(i => new VectorPoint(
                MessageId: 100 + i,
                Embedding: new float[] { 1f / (i + 1), 0f },
                ChannelId: 456,
                GuildId: 789,
                AuthorId: 999,
                CreatedAt: DateTimeOffset.UtcNow,
                Tags: new[] { $"tag{i}" }))
            .ToList();

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var results = await _store.SearchAsync(query, new VectorFilter(), topK: 3, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task SearchAsync_WithGuildIdFilter()
    {
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "a" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 456, 999, 999, DateTimeOffset.UtcNow, new[] { "b" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(GuildId: 789);
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(123));
    }

    [Test]
    public async Task SearchAsync_WithChannelIdFilter()
    {
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 111, 789, 999, DateTimeOffset.UtcNow, new[] { "a" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 222, 789, 999, DateTimeOffset.UtcNow, new[] { "b" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(ChannelId: 111);
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(123));
    }

    [Test]
    public async Task SearchAsync_WithAfterFilter()
    {
        var now = DateTimeOffset.UtcNow;
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 456, 789, 999, now.AddMinutes(-10), new[] { "old" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 456, 789, 999, now.AddMinutes(10), new[] { "new" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(After: now);
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(124));
    }

    [Test]
    public async Task SearchAsync_WithBeforeFilter()
    {
        var now = DateTimeOffset.UtcNow;
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 456, 789, 999, now.AddMinutes(-10), new[] { "old" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 456, 789, 999, now.AddMinutes(10), new[] { "new" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(Before: now);
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(123));
    }

    [Test]
    public async Task SearchAsync_WithAnyTagsFilter()
    {
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "general" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "specific" }),
            new VectorPoint(125, new float[] { 0.8f, 0.2f }, 456, 789, 999, DateTimeOffset.UtcNow, new[] { "general", "help" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(AnyTags: new[] { "general" });
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(2));
        var ids = results.Select(r => r.MessageId).ToList();
        Assert.That(ids, Contains.Item(123L));
        Assert.That(ids, Contains.Item(125L));
    }

    [Test]
    public async Task SearchAsync_WithMultipleFilters()
    {
        var now = DateTimeOffset.UtcNow;
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 111, 789, 999, now.AddMinutes(5), new[] { "tagged" }),
            new VectorPoint(124, new float[] { 0.9f, 0.1f }, 222, 789, 999, now.AddMinutes(10), new[] { "tagged" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(
            ChannelId: 111,
            After: now,
            AnyTags: new[] { "tagged" });

        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results.Count, Is.EqualTo(1));
        Assert.That(results[0].MessageId, Is.EqualTo(123));
    }

    [Test]
    public async Task SearchAsync_EmptyQuery_Throws()
    {
        var query = new ReadOnlyMemory<float>(Array.Empty<float>());
        Assert.ThrowsAsync<ArgumentException>(async () => await _store.SearchAsync(query, new VectorFilter(), topK: 10, CancellationToken.None));
    }

    [Test]
    public async Task SearchAsync_NegativeTopK_Throws()
    {
        var query = new ReadOnlyMemory<float>(new float[] { 1f });
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await _store.SearchAsync(query, new VectorFilter(), topK: -1, CancellationToken.None));
    }

    [Test]
    public async Task SearchAsync_NoMatches_ReturnsEmpty()
    {
        var points = new[]
        {
            new VectorPoint(123, new float[] { 1f, 0f }, 111, 789, 999, DateTimeOffset.UtcNow, new[] { "a" }),
        };

        await _store.UpsertManyAsync(points, CancellationToken.None);

        var query = new ReadOnlyMemory<float>(new float[] { 1f, 0f });
        var filter = new VectorFilter(ChannelId: 999);
        var results = await _store.SearchAsync(query, filter, topK: 10, CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task Clear_RemovesAllVectors()
    {
        var points = Enumerable.Range(1, 5)
            .Select(i => new VectorPoint(
                MessageId: 100 + i,
                Embedding: new float[] { 0.1f },
                ChannelId: 456,
                GuildId: 789,
                AuthorId: 999,
                CreatedAt: DateTimeOffset.UtcNow,
                Tags: new[] { $"tag{i}" }))
            .ToList();

        await _store.UpsertManyAsync(points, CancellationToken.None);
        Assert.That(_store.Count, Is.EqualTo(5));

        _store.Clear();
        Assert.That(_store.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ThreadSafety_ConcurrentUpserts()
    {
        var tasks = Enumerable.Range(1, 10)
            .Select(i => Task.Run(async () =>
            {
                var point = new VectorPoint(
                    MessageId: i,
                    Embedding: new float[] { 0.1f },
                    ChannelId: 456,
                    GuildId: 789,
                    AuthorId: 999,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Tags: new[] { $"tag{i}" });

                await _store.UpsertManyAsync(new[] { point }, CancellationToken.None);
            }))
            .ToList();

        await Task.WhenAll(tasks);
        Assert.That(_store.Count, Is.EqualTo(10));
    }
}
