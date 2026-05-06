namespace DiscordScraper.TestSupport.Stubs;

using DiscordScraper.Core.Vector;

/// <summary>
/// In-memory implementation of <see cref="IVectorStore"/> for unit-tier tests.
/// Stores vectors in a thread-safe dictionary and implements exact score-based search.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<long, VectorPoint> _points = new();
    private readonly object _lock = new();

    public Task UpsertManyAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points == null)
            throw new ArgumentNullException(nameof(points));

        lock (_lock)
        {
            foreach (var point in points)
            {
                _points[point.MessageId] = point;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> SearchAsync(
        ReadOnlyMemory<float> query,
        VectorFilter filter,
        int topK,
        CancellationToken ct)
    {
        if (topK < 0)
            throw new ArgumentOutOfRangeException(nameof(topK), "topK must be non-negative");
        if (query.IsEmpty)
            throw new ArgumentException("Query vector cannot be empty", nameof(query));

        lock (_lock)
        {
            var matches = _points.Values
                .Where(p => MatchesFilter(p, filter))
                .Select(p => new VectorMatch(
                    p.MessageId,
                    ComputeScore(query, p.Embedding),
                    p.ChannelId,
                    p.GuildId,
                    p.AuthorId,
                    p.CreatedAt,
                    p.Tags))
                .OrderByDescending(m => m.Score)
                .Take(topK)
                .ToList();

            return Task.FromResult<IReadOnlyList<VectorMatch>>(matches);
        }
    }

    /// <summary>
    /// Compute cosine similarity between two vectors. Assumes both are already normalized
    /// or unnormalized but comparable; the dot product serves as a similarity proxy.
    /// </summary>
    private static float ComputeScore(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;

        if (aSpan.Length != bSpan.Length)
            throw new InvalidOperationException(
                $"Vector dimensions must match: {aSpan.Length} vs {bSpan.Length}");

        float dotProduct = 0;
        float aMag = 0;
        float bMag = 0;

        for (int i = 0; i < aSpan.Length; i++)
        {
            dotProduct += aSpan[i] * bSpan[i];
            aMag += aSpan[i] * aSpan[i];
            bMag += bSpan[i] * bSpan[i];
        }

        var magProduct = MathF.Sqrt(aMag) * MathF.Sqrt(bMag);
        if (magProduct == 0)
            return 0;

        return dotProduct / magProduct;
    }

    /// <summary>
    /// Check if a vector point matches the filter criteria.
    /// </summary>
    private static bool MatchesFilter(VectorPoint point, VectorFilter filter)
    {
        if (filter.GuildId.HasValue && point.GuildId != filter.GuildId)
            return false;
        if (filter.ChannelId.HasValue && point.ChannelId != filter.ChannelId)
            return false;
        if (filter.After.HasValue && point.CreatedAt < filter.After)
            return false;
        if (filter.Before.HasValue && point.CreatedAt > filter.Before)
            return false;
        if (filter.AnyTags is not null && filter.AnyTags.Count > 0)
        {
            // Point matches if it has any tag in AnyTags
            if (!point.Tags.Any(t => filter.AnyTags.Contains(t)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Get the count of stored vectors (for testing/debugging).
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>
    /// Clear all stored vectors (for test cleanup).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _points.Clear();
        }
    }
}
