namespace DiscordScraper.TestSupport.Stubs;

/// <summary>
/// Abstract base for failure profiles that control whether each call faults.
/// Profiles decide whether to throw an exception given the call index and input.
/// </summary>
public abstract record FailureProfile<TIn>
{
    /// <summary>
    /// Determine whether the call at this index should fault.
    /// </summary>
    /// <param name="input">The input to this call.</param>
    /// <param name="callIndex">1-based call counter, incremented before this check.</param>
    /// <returns>Exception to throw, or null to proceed normally.</returns>
    public abstract Exception? FaultFor(TIn input, int callIndex);

    /// <summary>
    /// No failures; every call succeeds.
    /// </summary>
    public sealed record None : FailureProfile<TIn>
    {
        public override Exception? FaultFor(TIn _, int __) => null;
    }

    /// <summary>
    /// Fault every Nth call.
    /// </summary>
    public sealed record EveryNth(int N, Func<Exception> ExceptionFactory) : FailureProfile<TIn>
    {
        public override Exception? FaultFor(TIn _, int callIndex)
        {
            if (N <= 0)
                throw new ArgumentException($"N must be positive; got {N}");
            return callIndex % N == 0 ? ExceptionFactory() : null;
        }
    }

    /// <summary>
    /// Fault after N successful calls, then fault M consecutive calls.
    /// Reproduces the KillSwitch trip scenario: "after N normal calls,
    /// fault M consecutive calls to trip the circuit."
    /// </summary>
    public sealed record Burst(int AfterCalls, int Count, Func<Exception> ExceptionFactory) : FailureProfile<TIn>
    {
        public override Exception? FaultFor(TIn _, int callIndex)
        {
            if (AfterCalls < 0)
                throw new ArgumentException($"AfterCalls must be non-negative; got {AfterCalls}");
            if (Count <= 0)
                throw new ArgumentException($"Count must be positive; got {Count}");

            // Check if we're in the burst window: calls AfterCalls+1 through AfterCalls+Count
            return callIndex > AfterCalls && callIndex <= AfterCalls + Count
                ? ExceptionFactory()
                : null;
        }
    }

    /// <summary>
    /// Fault calls in the range [StartCall, EndCall] (inclusive).
    /// </summary>
    public sealed record Window(int StartCall, int EndCall, Func<Exception> ExceptionFactory) : FailureProfile<TIn>
    {
        public override Exception? FaultFor(TIn _, int callIndex)
        {
            if (StartCall < 0)
                throw new ArgumentException($"StartCall must be non-negative; got {StartCall}");
            if (EndCall < StartCall)
                throw new ArgumentException($"EndCall ({EndCall}) must be >= StartCall ({StartCall})");
            return callIndex >= StartCall && callIndex <= EndCall
                ? ExceptionFactory()
                : null;
        }
    }

    /// <summary>
    /// Fault decision computed from the input and call index.
    /// </summary>
    public sealed record FromInput(Func<TIn, int, Exception?> Selector) : FailureProfile<TIn>
    {
        public override Exception? FaultFor(TIn input, int callIndex) => Selector(input, callIndex);
    }
}
