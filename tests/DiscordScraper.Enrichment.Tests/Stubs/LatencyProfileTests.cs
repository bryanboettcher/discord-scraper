using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class LatencyProfileTests
{
    [Test]
    public async Task Constant_ZeroDuration_ReturnsImmediately()
    {
        var profile = new LatencyProfile<string>.Constant(TimeSpan.Zero);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await profile.Delay("test", CancellationToken.None);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(50));
    }

    [Test]
    public async Task Constant_FixedDuration_DelaysExactTime()
    {
        var profile = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await profile.Delay("test", CancellationToken.None);
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(100));
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200));
    }

    [Test]
    public async Task Range_UniformDistribution_ProducesValuesInRange()
    {
        var min = TimeSpan.FromMilliseconds(50);
        var max = TimeSpan.FromMilliseconds(150);
        var profile = new LatencyProfile<string>.Range(min, max);

        var measurements = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await profile.Delay("test", CancellationToken.None);
            sw.Stop();
            measurements.Add(sw.ElapsedMilliseconds);
        }

        Assert.That(measurements, Has.Count.EqualTo(10));
        foreach (var m in measurements)
        {
            Assert.That(m, Is.GreaterThanOrEqualTo(50));
            Assert.That(m, Is.LessThan(200));
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
        var profile = new LatencyProfile<string>.Lognormal(TimeSpan.FromMilliseconds(50), 0.5);

        var measurements = new List<long>();
        for (int i = 0; i < 20; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await profile.Delay("test", CancellationToken.None);
            sw.Stop();
            measurements.Add(sw.ElapsedMilliseconds);
        }

        Assert.That(measurements, Is.Not.Empty);
        Assert.That(measurements.Min(), Is.GreaterThanOrEqualTo(0));
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
        var profile = new LatencyProfile<string>.Drift(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        var measurements = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await profile.Delay("test", CancellationToken.None);
            sw.Stop();
            measurements.Add(sw.ElapsedMilliseconds);
        }

        Assert.That(measurements, Has.Count.EqualTo(5));
        Assert.That(measurements[0], Is.LessThan(measurements[1]));
        Assert.That(measurements[1], Is.LessThan(measurements[2]));
    }

    [Test]
    public async Task FromInput_ComputesLatencyFromInput()
    {
        var profile = new LatencyProfile<string>.FromInput(input =>
            input.Length > 5 ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero);

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        await profile.Delay("short", CancellationToken.None);
        sw1.Stop();

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        await profile.Delay("verylongstring", CancellationToken.None);
        sw2.Stop();

        Assert.That(sw1.ElapsedMilliseconds, Is.LessThan(20));
        Assert.That(sw2.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(50));
    }

    [Test]
    public async Task Drift_IsStatefulAcrossMultipleCalls()
    {
        var profile = new LatencyProfile<string>.Drift(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5));

        long prev = 0;
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await profile.Delay("test", CancellationToken.None);
            sw.Stop();
            if (i > 0)
            {
                Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(prev));
            }
            prev = sw.ElapsedMilliseconds;
        }
    }
}
