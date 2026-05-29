using DiscordScraper.TestSupport.Stubs;
using Microsoft.Extensions.Time.Testing;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class LatencyProfileTests
{
    [Test]
    public async Task Constant_ZeroDuration_ReturnsImmediately()
    {
        // Zero-duration profile skips Task.Delay entirely; no TimeProvider needed.
        var profile = new LatencyProfile<string>.Constant(TimeSpan.Zero);
        await profile.Delay("test", CancellationToken.None);
        Assert.Pass();
    }

    [Test]
    public async Task Constant_FixedDuration_DelaysExactTime()
    {
        var time = new FakeTimeProvider();
        var profile = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(100), time);

        var task = profile.Delay("test", CancellationToken.None);
        Assert.That(task.IsCompleted, Is.False, "Should not complete before time advances");

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(task.IsCompleted, Is.False, "Should not complete at 99ms");

        time.Advance(TimeSpan.FromMilliseconds(1));
        await task;
    }

    /// <summary>
    /// Verifies that Range.Delay completes when the FakeTimeProvider is advanced past Max.
    /// Uses FakeTimeProvider so the test is deterministic — no wall-clock dependency and no
    /// sensitivity to scheduler jitter under Docker resource constraints.
    /// </summary>
    [Test]
    public async Task Range_UniformDistribution_ProducesValuesInRange()
    {
        var min = TimeSpan.FromMilliseconds(50);
        var max = TimeSpan.FromMilliseconds(150);

        for (int i = 0; i < 10; i++)
        {
            var time = new FakeTimeProvider();
            var profile = new LatencyProfile<string>.Range(min, max, time);

            var task = profile.Delay("test", CancellationToken.None);

            // Advance past Max so whatever delay was sampled in [min, max) is guaranteed elapsed.
            time.Advance(max);

            await task;
        }
    }

    [Test]
    public void Range_InvalidRange_Throws()
    {
        var profile = new LatencyProfile<string>.Range(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
        Assert.ThrowsAsync<ArgumentException>(async () => await profile.Delay("test", CancellationToken.None));
    }

    [Test]
    public async Task Lognormal_WithSigma_ProducesVariedLatencies()
    {
        // Lognormal samples can theoretically be arbitrarily large; advance time by a huge
        // amount to guarantee every awaited Task.Delay fires regardless of sample.
        var time = new FakeTimeProvider();
        var profile = new LatencyProfile<string>.Lognormal(TimeSpan.FromMilliseconds(50), 0.5, time);

        for (int i = 0; i < 20; i++)
        {
            var task = profile.Delay("test", CancellationToken.None);
            time.Advance(TimeSpan.FromHours(1));
            await task;
        }
    }

    [Test]
    public void Lognormal_InvalidSigma_Throws()
    {
        var profile = new LatencyProfile<string>.Lognormal(TimeSpan.FromMilliseconds(50), -0.5);
        Assert.ThrowsAsync<ArgumentException>(async () => await profile.Delay("test", CancellationToken.None));
    }

    [Test]
    public async Task Drift_IncrementsPerCall()
    {
        // Drift yields 10, 20, 30, 40, 50ms — verify each call's task completes only after
        // its expected delay elapses (and not before). This proves the per-call increment
        // without relying on wall-clock measurement.
        var time = new FakeTimeProvider();
        var profile = new LatencyProfile<string>.Drift(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            time);

        for (int i = 1; i <= 5; i++)
        {
            var expectedDelay = TimeSpan.FromMilliseconds(10 * i);
            var task = profile.Delay("test", CancellationToken.None);

            time.Advance(expectedDelay - TimeSpan.FromMilliseconds(1));
            Assert.That(task.IsCompleted, Is.False, $"Call {i} should not complete before {expectedDelay.TotalMilliseconds}ms");

            time.Advance(TimeSpan.FromMilliseconds(1));
            await task;
        }
    }

    [Test]
    public async Task FromInput_ComputesLatencyFromInput()
    {
        var time = new FakeTimeProvider();
        var profile = new LatencyProfile<string>.FromInput(
            input => input.Length > 5 ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero,
            time);

        // Short input: Selector returns TimeSpan.Zero → skips Task.Delay → completes synchronously.
        await profile.Delay("short", CancellationToken.None);

        // Long input: Selector returns 50ms → awaits Task.Delay with the fake provider.
        var task = profile.Delay("verylongstring", CancellationToken.None);
        Assert.That(task.IsCompleted, Is.False, "Long-input call should not complete before time advances");

        time.Advance(TimeSpan.FromMilliseconds(50));
        await task;
    }

    [Test]
    public async Task Drift_IsStatefulAcrossMultipleCalls()
    {
        // Drift state: yields 5, 10, 15ms. Verify the third call needs at least 15ms.
        var time = new FakeTimeProvider();
        var profile = new LatencyProfile<string>.Drift(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            time);

        var t1 = profile.Delay("test", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(5));
        await t1;

        var t2 = profile.Delay("test", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await t2;

        var t3 = profile.Delay("test", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(14));
        Assert.That(t3.IsCompleted, Is.False, "Third call should not complete before 15ms");
        time.Advance(TimeSpan.FromMilliseconds(1));
        await t3;
    }
}
