namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// Stateless helpers for percentile computation over gap lists produced by
/// <see cref="ITestObservationSink"/>. These are intentionally static so
/// tests can use them without holding a sink reference.
/// </summary>
public static class ObservationMetrics
{
    /// <summary>
    /// Computes the given percentile from <paramref name="gaps"/>.
    /// <paramref name="percentile"/> must be in [0, 100].
    /// Returns <see cref="TimeSpan.Zero"/> for an empty list.
    /// </summary>
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> gaps, double percentile)
    {
        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in [0, 100]");
        if (gaps.Count == 0)
            return TimeSpan.Zero;

        var sorted = gaps.OrderBy(g => g).ToArray();
        if (percentile == 0) return sorted[0];
        if (percentile == 100) return sorted[^1];

        // Nearest-rank method — simpler and unambiguous for test assertions
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[rank - 1];
    }

    /// <summary>Convenience overload: p50 median.</summary>
    public static TimeSpan P50(IReadOnlyList<TimeSpan> gaps) => Percentile(gaps, 50);

    /// <summary>Convenience overload: p95.</summary>
    public static TimeSpan P95(IReadOnlyList<TimeSpan> gaps) => Percentile(gaps, 95);

    /// <summary>Convenience overload: p99.</summary>
    public static TimeSpan P99(IReadOnlyList<TimeSpan> gaps) => Percentile(gaps, 99);
}
