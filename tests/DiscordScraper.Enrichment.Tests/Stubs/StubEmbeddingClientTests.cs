using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class StubEmbeddingClientTests
{
    [Test]
    public void Constructor_Default_UsesDefaultProfiles()
    {
        var stub = new StubEmbeddingClient();
        Assert.That(stub.Model, Is.EqualTo("stub-embed"));
    }

    [Test]
    public async Task EmbedAsync_WithDefaultProfiles_ReturnsEmbedding()
    {
        var stub = new StubEmbeddingClient();
        var result = await stub.EmbedAsync("test text");

        Assert.That(result.Length, Is.EqualTo(768));
        var result2 = await stub.EmbedAsync("test text");
        Assert.That(result.Span.SequenceEqual(result2.Span), Is.True);
    }

    [Test]
    public async Task EmbedAsync_WithConstantLatency_DelaysCorrectly()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var latency = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(100), time);
        var stub = new StubEmbeddingClient(
            latency,
            new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicEmbedding());

        var task = stub.EmbedAsync("test");
        Assert.That(task.IsCompleted, Is.False, "Should not complete before time advances past latency");

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(task.IsCompleted, Is.False, "Should not complete at 99ms");

        time.Advance(TimeSpan.FromMilliseconds(1));
        await task;
    }

    [Test]
    public async Task EmbedAsync_WithFailureProfile_ThrowsOnFailure()
    {
        var failure = new FailureProfile<string>.EveryNth(1, () => new InvalidOperationException("Injected failure"));
        var stub = new StubEmbeddingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicEmbedding());

        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.EmbedAsync("test"));
    }

    [Test]
    public async Task EmbedAsync_FailureBeforeLatency_FailsImmediately()
    {
        var failure = new FailureProfile<string>.EveryNth(1, () => new InvalidOperationException("Injected failure"));
        var latency = new LatencyProfile<string>.Constant(TimeSpan.FromMilliseconds(500));
        var stub = new StubEmbeddingClient(latency, failure, OutputGeneratorHelpers.DeterministicEmbedding());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.EmbedAsync("test"));
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200));
    }

    [Test]
    public async Task EmbedAsync_WithBurstFailure_FailsAtCorrectCall()
    {
        var failure = new FailureProfile<string>.Burst(1, 2, () => new InvalidOperationException("Burst failure"));
        var stub = new StubEmbeddingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicEmbedding());

        var result1 = await stub.EmbedAsync("test1");
        Assert.That(result1.Length, Is.EqualTo(768));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.EmbedAsync("test2"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await stub.EmbedAsync("test3"));

        var result4 = await stub.EmbedAsync("test4");
        Assert.That(result4.Length, Is.EqualTo(768));
    }

    [Test]
    public async Task EmbedAsync_WithFromInputLatency_VariesByInput()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var latency = new LatencyProfile<string>.FromInput(
            input => input.Length > 5 ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero,
            time);

        var stub = new StubEmbeddingClient(latency, new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicEmbedding());

        // Short input → Selector returns Zero → no Task.Delay → completes synchronously.
        await stub.EmbedAsync("short");

        // Long input → Selector returns 50ms → awaits Task.Delay against FakeTimeProvider.
        var task = stub.EmbedAsync("verylongstring");
        Assert.That(task.IsCompleted, Is.False, "Long-input embed should not complete before time advances");

        time.Advance(TimeSpan.FromMilliseconds(50));
        await task;
    }

    [Test]
    public async Task EmbedAsync_CallIndexIncrementsPerCall()
    {
        var indices = new List<int>();
        var failure = new FailureProfile<string>.FromInput((_, idx) =>
        {
            indices.Add(idx);
            return null;
        });

        var stub = new StubEmbeddingClient(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            failure,
            OutputGeneratorHelpers.DeterministicEmbedding());

        await stub.EmbedAsync("test1");
        await stub.EmbedAsync("test2");
        await stub.EmbedAsync("test3");

        Assert.That(indices, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task EmbedAsync_DeterministicOutputAcrossMultipleCalls()
    {
        var stub = new StubEmbeddingClient();

        var outputs = new List<ReadOnlyMemory<float>>();
        for (int i = 0; i < 3; i++)
        {
            outputs.Add(await stub.EmbedAsync("same-input"));
        }

        Assert.That(outputs[0].Span.SequenceEqual(outputs[1].Span), Is.True);
        Assert.That(outputs[1].Span.SequenceEqual(outputs[2].Span), Is.True);
    }
}
