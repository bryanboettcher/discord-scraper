namespace DiscordScraper.TestSupport.Stubs;

using DiscordScraper.Enrichment.Ollama;

/// <summary>
/// Test stub for <see cref="IEmbeddingClient"/> with injectable latency, failure,
/// and output generation profiles. Composes three orthogonal knobs:
/// - <see cref="LatencyProfile{TIn}"/>: when the call returns
/// - <see cref="FailureProfile{TIn}"/>: whether the call faults
/// - <see cref="OutputGenerator{TIn, TOut}"/>: what the call returns if it succeeds
/// </summary>
public sealed class StubEmbeddingClient : IEmbeddingClient
{
    private readonly LatencyProfile<string> _latency;
    private readonly FailureProfile<string> _failure;
    private readonly OutputGenerator<string, ReadOnlyMemory<float>> _output;
    private int _callIndex;

    /// <summary>
    /// Create a stub embedding client with default profiles:
    /// - Latency: <see cref="LatencyProfile{TIn}.Constant"/> with 0ms
    /// - Failure: <see cref="FailureProfile{TIn}.None"/>
    /// - Output: <see cref="OutputGeneratorHelpers.DeterministicEmbedding"/> with 768 dimensions
    /// </summary>
    public StubEmbeddingClient()
        : this(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicEmbedding(768))
    {
    }

    /// <summary>
    /// Create a stub embedding client with specified profiles.
    /// </summary>
    public StubEmbeddingClient(
        LatencyProfile<string> latency,
        FailureProfile<string> failure,
        OutputGenerator<string, ReadOnlyMemory<float>> output)
    {
        _latency = latency ?? throw new ArgumentNullException(nameof(latency));
        _failure = failure ?? throw new ArgumentNullException(nameof(failure));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Model => "stub-embed";

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref _callIndex);

        // Check for failure first (before paying latency cost)
        var fault = _failure.FaultFor(text, index);
        if (fault != null)
            throw fault;

        // Apply latency
        await _latency.Delay(text, ct);

        // Generate and return output
        return _output.Generate(text);
    }
}
