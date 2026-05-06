namespace DiscordScraper.TestSupport.Stubs;

using DiscordScraper.Enrichment.Ollama;

/// <summary>
/// Test stub for <see cref="ITaggingClient"/> with injectable latency, failure,
/// and output generation profiles. Composes three orthogonal knobs:
/// - <see cref="LatencyProfile{TIn}"/>: when the call returns
/// - <see cref="FailureProfile{TIn}"/>: whether the call faults
/// - <see cref="OutputGenerator{TIn, TOut}"/>: what the call returns if it succeeds
/// </summary>
public sealed class StubTaggingClient : ITaggingClient
{
    private readonly LatencyProfile<string> _latency;
    private readonly FailureProfile<string> _failure;
    private readonly OutputGenerator<string, TagResult> _output;
    private int _callIndex;

    /// <summary>
    /// Create a stub tagging client with default profiles:
    /// - Latency: <see cref="LatencyProfile{TIn}.Constant"/> with 0ms
    /// - Failure: <see cref="FailureProfile{TIn}.None"/>
    /// - Output: <see cref="OutputGeneratorHelpers.DeterministicTags"/> with 3 tags
    /// </summary>
    public StubTaggingClient()
        : this(
            new LatencyProfile<string>.Constant(TimeSpan.Zero),
            new FailureProfile<string>.None(),
            OutputGeneratorHelpers.DeterministicTags(3))
    {
    }

    /// <summary>
    /// Create a stub tagging client with specified profiles.
    /// </summary>
    public StubTaggingClient(
        LatencyProfile<string> latency,
        FailureProfile<string> failure,
        OutputGenerator<string, TagResult> output)
    {
        _latency = latency ?? throw new ArgumentNullException(nameof(latency));
        _failure = failure ?? throw new ArgumentNullException(nameof(failure));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Model => "stub-tag";

    public async Task<TagResult> TagAsync(string content, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref _callIndex);

        // Check for failure first (before paying latency cost)
        var fault = _failure.FaultFor(content, index);
        if (fault != null)
            throw fault;

        // Apply latency
        await _latency.Delay(content, ct);

        // Generate and return output
        return _output.Generate(content);
    }
}
