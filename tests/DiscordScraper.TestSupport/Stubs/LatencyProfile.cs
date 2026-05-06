namespace DiscordScraper.TestSupport.Stubs;

/// <summary>
/// Abstract base for latency profiles that control when each call returns.
/// Profiles are input-independent (stateful stream) or input-dependent.
/// </summary>
public abstract record LatencyProfile<TIn>
{
    public abstract ValueTask Delay(TIn input, CancellationToken ct);

    /// <summary>
    /// Fixed latency for every call.
    /// </summary>
    public sealed record Constant(TimeSpan Duration) : LatencyProfile<TIn>
    {
        public override async ValueTask Delay(TIn _, CancellationToken ct)
        {
            if (Duration > TimeSpan.Zero)
                await Task.Delay(Duration, ct);
        }
    }

    /// <summary>
    /// Latency sampled uniformly from [Min, Max) per call.
    /// </summary>
    public sealed record Range(TimeSpan Min, TimeSpan Max) : LatencyProfile<TIn>
    {
        public override async ValueTask Delay(TIn _, CancellationToken ct)
        {
            if (Min >= Max)
                throw new ArgumentException($"Min ({Min}) must be less than Max ({Max})");
            var rng = new Random();
            var delay = Min.Add(TimeSpan.FromMilliseconds(
                rng.NextDouble() * (Max - Min).TotalMilliseconds));
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }
    }

    /// <summary>
    /// Latency sampled from a lognormal distribution with given median and sigma.
    /// Median is the 50th percentile of the latency distribution.
    /// Sigma controls the spread; typical values 0.2–1.0.
    /// </summary>
    public sealed record Lognormal(TimeSpan Median, double Sigma) : LatencyProfile<TIn>
    {
        public override async ValueTask Delay(TIn _, CancellationToken ct)
        {
            if (Sigma <= 0)
                throw new ArgumentException($"Sigma must be positive; got {Sigma}");
            var mu = Math.Log(Median.TotalMilliseconds);
            var rng = new Random();
            // Box-Muller to normal(0,1), then scale by sigma and shift by mu
            var u1 = rng.NextDouble();
            var u2 = rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            var logMs = mu + Sigma * z;
            var delayMs = Math.Exp(logMs);
            if (delayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
        }
    }

    /// <summary>
    /// Latency that drifts: starts at <see cref="Start"/>, increases by
    /// <see cref="PerCall"/> on each call. Useful for approximating warmup ramps
    /// or memory-pressure tails.
    /// </summary>
    public sealed record Drift(TimeSpan Start, TimeSpan PerCall) : StreamLatencyProfile<TIn>
    {
        protected override IEnumerable<TimeSpan> GenerateDelays()
        {
            var d = Start;
            while (true)
            {
                yield return d;
                d += PerCall;
            }
        }
    }

    /// <summary>
    /// Latency computed from the input. Allows per-payload latency decisions.
    /// </summary>
    public sealed record FromInput(Func<TIn, TimeSpan> Selector) : LatencyProfile<TIn>
    {
        public override async ValueTask Delay(TIn input, CancellationToken ct)
        {
            var delay = Selector(input);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }
    }
}

/// <summary>
/// Internal intermediate base for the four input-independent latency profiles
/// that reduce to a stream of timespans: <see cref="LatencyProfile{TIn}.Constant"/>,
/// <see cref="LatencyProfile{TIn}.Range"/>, <see cref="LatencyProfile{TIn}.Lognormal"/>,
/// and <see cref="LatencyProfile{TIn}.Drift"/>. This base implements <see cref="LatencyProfile{TIn}.Delay"/>
/// via <see cref="GenerateDelays"/>, allowing cleaner state management for
/// stateful profiles like <c>Drift</c>.
/// </summary>
public abstract record StreamLatencyProfile<TIn> : LatencyProfile<TIn>
{
    private readonly IEnumerator<TimeSpan> _delays;

    protected StreamLatencyProfile()
    {
        _delays = GenerateDelays().GetEnumerator();
    }

    protected abstract IEnumerable<TimeSpan> GenerateDelays();

    public sealed override async ValueTask Delay(TIn _, CancellationToken ct)
    {
        lock (_delays)
        {
            if (!_delays.MoveNext())
                throw new InvalidOperationException("Delay stream exhausted");
        }

        var delay = _delays.Current;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }
}
