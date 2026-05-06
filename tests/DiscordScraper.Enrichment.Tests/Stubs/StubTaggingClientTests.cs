using DiscordScraper.Enrichment.Ollama;
using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class StubTaggingClientTests
{
    [Test]
    public void Constructor_Default_UsesDefaultProfiles()
    {
        var stub = new StubTaggingClient();
        Assert.That(stub.Model, Is.EqualTo("stub-tag"));
    }

    [Test]
    public async Task TagAsync_WithDefaultProfiles_ReturnsTagResult()
    {
        var stub = new StubTaggingClient();
        var result = await stub.TagAsync("test text");

        Assert.That(result.TopicTags.Count, Is.EqualTo(3));
        Assert.That(result.IsSubstantive, Is.True);
    }

    [Test]
    public async Task TagAsync_WithConstantLatency_DelaysCorrectly()
    {
        var latency = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(100));
        var stub = new StubTaggingClient(
            latency,
            new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicTags(3));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stub.TagAsync("test");
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(100));
    }

    [Test]
    public async Task TagAsync_WithFailureProfile_ThrowsOnFailure()
    {
        var failure = new FailureProfile<string>.EveryNth(1, () => new InvalidOperationException("Injected failure"));
        var stub = new StubTaggingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicTags(3));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.TagAsync("test"));
    }

    [Test]
    public async Task TagAsync_FailureBeforeLatency_FailsImmediately()
    {
        var failure = new FailureProfile<string>.EveryNth(1, () => new InvalidOperationException("Injected failure"));
        var latency = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(500));
        var stub = new StubTaggingClient(latency, failure, OutputGeneratorHelpers.DeterministicTags(3));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.TagAsync("test"));
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200));
    }

    [Test]
    public async Task TagAsync_WithBurstFailure_FailsAtCorrectCall()
    {
        var failure = new FailureProfile<string>.Burst(1, 2, () => new InvalidOperationException("Burst failure"));
        var stub = new StubTaggingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicTags(3));

        var result1 = await stub.TagAsync("test1");
        Assert.That(result1.TopicTags.Count, Is.GreaterThan(0));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.TagAsync("test2"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.TagAsync("test3"));

        var result4 = await stub.TagAsync("test4");
        Assert.That(result4.TopicTags.Count, Is.GreaterThan(0));
    }

    [Test]
    public async Task TagAsync_WithFromInputLatency_VariesByInput()
    {
        var latency = new LatencyProfile<string>.FromInput(input =>
            input.Length > 5 ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero);

        var stub = new StubTaggingClient(
            latency,
            new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicTags(3));

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        await stub.TagAsync("short");
        sw1.Stop();

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        await stub.TagAsync("verylongstring");
        sw2.Stop();

        Assert.That(sw1.ElapsedMilliseconds, Is.LessThan(30));
        Assert.That(sw2.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(50));
    }

    [Test]
    public async Task TagAsync_CallIndexIncrementsPerCall()
    {
        var indices = new List<int>();
        var failure = new FailureProfile<string>.FromInput((_, idx) =>
        {
            indices.Add(idx);
            return null;
        });

        var stub = new StubTaggingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicTags(3));

        await stub.TagAsync("test1");
        await stub.TagAsync("test2");
        await stub.TagAsync("test3");

        Assert.That(indices, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task TagAsync_DeterministicOutputAcrossMultipleCalls()
    {
        var stub = new StubTaggingClient();

        var results = new List<TagResult>();
        for (int i = 0; i < 3; i++)
        {
            results.Add(await stub.TagAsync("same-input"));
        }

        // Verify all have the same tags and substantiveness
        Assert.That(results[0].TopicTags, Is.EqualTo(results[1].TopicTags));
        Assert.That(results[1].TopicTags, Is.EqualTo(results[2].TopicTags));
        Assert.That(results[0].IsSubstantive, Is.EqualTo(results[1].IsSubstantive));
        Assert.That(results[1].IsSubstantive, Is.EqualTo(results[2].IsSubstantive));
    }

    [Test]
    public async Task TagAsync_KillSwitchScenario()
    {
        var failure = new FailureProfile<string>.Burst(10, 5, () => new InvalidOperationException("KillSwitch"));
        var stub = new StubTaggingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicTags(3));

        var successCount = 0;
        var failureCount = 0;

        for (int i = 1; i <= 20; i++)
        {
            try
            {
                await stub.TagAsync($"msg{i}");
                successCount++;
            }
            catch (InvalidOperationException)
            {
                failureCount++;
            }
        }

        Assert.That(successCount, Is.EqualTo(15));
        Assert.That(failureCount, Is.EqualTo(5));
    }
}
