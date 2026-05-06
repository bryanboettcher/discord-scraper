# DiscordScraper.TestSupport

Test infrastructure for integration and E2E tests. Composes capture/replay (Task A),
stub services (Task B), and MT observers (Task C) into a single `TestStack` surface.

## Tiers

| Tier | Transport | Saga repo | Containers | Target |
|------|-----------|-----------|------------|--------|
| `Unit()` | InMemory | In-memory | None | <1s |
| `Integration()` | InMemory | Mongo | MongoDB | Seconds |
| `E2E()` | RabbitMQ | Mongo | Mongo + RabbitMQ + Postgres | Tens of seconds |

## Basic usage

```csharp
// Unit tier — no containers, no fixtures, zero-latency stubs
await using var stack = TestStack.Unit();
await stack.StartAsync();
await stack.GetTestHarness().Bus.Publish<MessageCaptured>(new { ... });
await stack.PumpUntilQuiescent(TimeSpan.FromSeconds(5));

// Integration tier — fixture replay, Mongo saga repo
await using var stack = TestStack.Integration()
    .WithFixture("synthetic_small.jsonl")
    .WithRequestTimeout(TimeSpan.FromSeconds(15));
await stack.StartAsync();
await stack.PumpUntilQuiescent(TimeSpan.FromSeconds(60));

var harness = stack.GetTestHarness();
harness.Consumed.Select<MessageCaptured>().Count().ShouldBe(10);
stack.Observations.AssertNoGapsExceeding(TimeSpan.FromSeconds(15));
```

## Knob overrides

```csharp
// Drift latency — starts at 50ms, grows 50ms per embedding call
.WithTaggingLatency(new LatencyProfile<string>.Drift(
    Start: TimeSpan.FromMilliseconds(50),
    PerCall: TimeSpan.FromMilliseconds(50)))

// Periodic failure — every 3rd embedding call throws
.WithEmbeddingFailure(new FailureProfile<string>.EveryNth(
    N: 3,
    ExceptionFactory: () => new InvalidOperationException("stub failure")))

// Custom output — deterministic 768-dim embedding from text hash
.WithEmbeddingOutput(OutputGeneratorHelpers.DeterministicEmbedding(768))
```

## Observation sink

The sink accumulates send and consume events. Useful properties:

```csharp
stack.Observations.Sends          // keyed by RequestId
stack.Observations.Consumes       // keyed by RequestId
stack.Observations.QueueDwells    // keyed by MessageId (read-side)
stack.Observations.AssertNoGapsExceeding(TimeSpan.FromSeconds(5))
stack.Observations.AssertAllConsumedWithin(TimeSpan.FromSeconds(10))
stack.Observations.GapsForType<TagMessageRequest>()
ObservationMetrics.P50(gaps)
ObservationMetrics.P99(gaps)
```

**Known limitation (Q1):** `ISendObserver` connected via `IBus.ConnectSendObserver` does not
fire for saga-initiated `Request()` sends under InMemory transport. `Observations.Sends` will
be empty in `Unit()` and `Integration()` tiers. `AssertNoGapsExceeding` trivially passes (no
pairs). Use `harness.Consumed.Select<T>()` for consume-side assertions in these tiers. The
`E2E()` tier (RabbitMQ) captures sends through the broker and populates the send side.

**Q3 — SentTime per transport:** InMemory transport sets `SentTime = null`; `QueueDwellObserver`
records `EnqueuedAt = DateTimeOffset.MinValue` and the dwell value is not meaningful. RabbitMQ
populates `SentTime` with broker accept time; dwell is meaningful. Filter dwells by
`EnqueuedAt != DateTimeOffset.MinValue` to exclude InMemory records.

## Fixtures

Fixtures are JSONL files embedded as `<EmbeddedResource>` in this project (or a consuming test
project). The `WithFixture("file.jsonl")` call searches the TestSupport assembly by suffix.

```csharp
// From TestSupport assembly (built-in synthetic fixture)
.WithFixture("synthetic_small.jsonl")

// From a consuming test assembly
.WithFixture(typeof(MyTest).Assembly, "my_fixture.jsonl")
```

The synthetic fixture (`synthetic_small.jsonl`) contains 10 messages from a synthetic guild.
One ("ok") is non-substantive and exits at `Excluded`; the other 9 run the full pipeline.

## Containers

Integration and E2E tiers start containers via Testcontainers. Docker or Podman must be
available. Tag container-dependent tests `[Category("Integration")]` and filter them out of
the default `dotnet test` run:

```bash
dotnet test DiscordScraper.slnx --filter "Category!=Integration"  # fast suite
dotnet test DiscordScraper.E2E.slnx                                # full suite
```
