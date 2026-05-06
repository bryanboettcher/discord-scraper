using DiscordScraper.TestSupport.Observers;

namespace DiscordScraper.Write.Tests.Observers;

/// <summary>
/// Unit tests for <see cref="ObservationMetrics"/> percentile computation.
/// </summary>
[TestFixture]
public sealed class ObservationMetricsTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Test]
    public void Percentile_EmptyList_ReturnsZero()
        => ObservationMetrics.Percentile([], 50).ShouldBe(TimeSpan.Zero);

    [Test]
    public void Percentile_SingleElement_AlwaysReturnsThatElement()
    {
        var gaps = new[] { Ms(42) };
        ObservationMetrics.Percentile(gaps, 0).ShouldBe(Ms(42));
        ObservationMetrics.Percentile(gaps, 50).ShouldBe(Ms(42));
        ObservationMetrics.Percentile(gaps, 100).ShouldBe(Ms(42));
    }

    [Test]
    public void Percentile_P0_ReturnsMin()
    {
        var gaps = new[] { Ms(10), Ms(5), Ms(1), Ms(100) };
        ObservationMetrics.Percentile(gaps, 0).ShouldBe(Ms(1));
    }

    [Test]
    public void Percentile_P100_ReturnsMax()
    {
        var gaps = new[] { Ms(10), Ms(5), Ms(1), Ms(100) };
        ObservationMetrics.Percentile(gaps, 100).ShouldBe(Ms(100));
    }

    [Test]
    public void P50_FiveElements_ReturnsMedian()
    {
        // Sorted: 1, 2, 3, 4, 5  → nearest-rank p50: ceil(0.5 * 5) = 3 → Ms(3)
        var gaps = new[] { Ms(3), Ms(1), Ms(5), Ms(2), Ms(4) };
        ObservationMetrics.P50(gaps).ShouldBe(Ms(3));
    }

    [Test]
    public void P95_HundredElements_ReturnsFifthFromEnd()
    {
        // 100 items, sorted 1..100ms. nearest-rank p95: ceil(0.95 * 100) = 95 → Ms(95)
        var gaps = Enumerable.Range(1, 100).Select(i => Ms(i)).ToList();
        ObservationMetrics.P95(gaps).ShouldBe(Ms(95));
    }

    [Test]
    public void P99_HundredElements_ReturnsNinetyNinth()
    {
        // nearest-rank p99: ceil(0.99 * 100) = 99 → Ms(99)
        var gaps = Enumerable.Range(1, 100).Select(i => Ms(i)).ToList();
        ObservationMetrics.P99(gaps).ShouldBe(Ms(99));
    }

    [Test]
    public void Percentile_OutOfRangeBelow_Throws()
        => Should.Throw<ArgumentOutOfRangeException>(() => ObservationMetrics.Percentile([], -1));

    [Test]
    public void Percentile_OutOfRangeAbove_Throws()
        => Should.Throw<ArgumentOutOfRangeException>(() => ObservationMetrics.Percentile([], 101));
}
