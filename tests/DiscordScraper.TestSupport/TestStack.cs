using System.Reflection;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Requests;
using DiscordScraper.Core.Vector;
using DiscordScraper.Enrichment.Ollama;
using DiscordScraper.TestSupport.Capture;
using DiscordScraper.TestSupport.Observers;
using DiscordScraper.TestSupport.Stubs;
using DiscordScraper.Write;
using DiscordScraper.Write.Parsing;
using DiscordScraper.Write.Repositories;
using DiscordScraper.Write.Sagas;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace DiscordScraper.TestSupport;

/// <summary>
/// Tier-factory surface for integration and E2E tests. Composes Tasks A/B/C into a
/// single disposable test context. Three tiers:
///
/// <list type="bullet">
///   <item><see cref="Unit"/> — InMemory MT + stubs + observers + in-memory vector store. No containers.</item>
///   <item><see cref="Integration"/> — InMemory MT + stubs + Mongo (saga repo) via Testcontainers.</item>
///   <item><see cref="E2E"/> — RabbitMQ + Mongo + Postgres via Testcontainers + stubs + observers.</item>
/// </list>
///
/// Typical usage:
/// <code>
/// await using var stack = TestStack.Integration()
///     .WithFixture("synthetic_small.jsonl");
/// await stack.StartAsync();
/// await stack.PumpUntilQuiescent(TimeSpan.FromSeconds(30));
/// stack.Observations.AssertNoGapsExceeding(TimeSpan.FromSeconds(5));
/// </code>
/// </summary>
public sealed class TestStack : IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Internal tier configuration
    // -------------------------------------------------------------------------

    private readonly TestStackTier _tier;
    private readonly List<IAsyncDisposable> _containers = [];
    private ServiceProvider? _provider;

    // Knob overrides — null means use the default stub
    private LatencyProfile<string>? _embeddingLatency;
    private FailureProfile<string>? _embeddingFailure;
    private OutputGenerator<string, ReadOnlyMemory<float>>? _embeddingOutput;

    private LatencyProfile<string>? _taggingLatency;
    private FailureProfile<string>? _taggingFailure;
    private OutputGenerator<string, TagResult>? _taggingOutput;

    // Request timeout for the saga — default 10s in test (vs 30s prod) to exercise timeout paths faster
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    // Fixture envelopes to replay (populated via WithFixture)
    private IEnumerable<CaptureEnvelope>? _fixtureEnvelopes;

    // Connection info for containers (filled by StartContainersAsync)
    private string? _mongoConnectionString;
    private string? _rabbitConnectionString;

    private ITestHarness? _harness;
    private ConnectHandle[]? _observerHandles;

    private enum TestStackTier { Unit, Integration, E2E }

    // -------------------------------------------------------------------------
    // Factory methods
    // -------------------------------------------------------------------------

    private TestStack(TestStackTier tier) => _tier = tier;

    /// <summary>
    /// MT InMemoryTestHarness + stubs (default knobs) + observers + in-memory IVectorStore.
    /// No containers. Target: &lt;1s per test.
    /// </summary>
    public static TestStack Unit() => new(TestStackTier.Unit);

    /// <summary>
    /// MT InMemory transport + stubs + Mongo via Testcontainers (saga Mongo repo).
    /// Fixture replay available. Target: seconds.
    /// </summary>
    public static TestStack Integration() => new(TestStackTier.Integration);

    /// <summary>
    /// RabbitMQ + Mongo via Testcontainers + stubs + observers + replay fixtures.
    /// Full message-bus stack. Target: tens of seconds.
    /// Note: Postgres (pgvector) uses the InMemoryVectorStore stub — the real Postgres vector
    /// store requires the full DiscordScraper.Read pipeline wiring which is beyond TestStack scope.
    /// </summary>
    public static TestStack E2E() => new(TestStackTier.E2E);

    // -------------------------------------------------------------------------
    // Fluent configuration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads a fixture from an embedded resource in the TestSupport assembly by suffix.
    /// </summary>
    public TestStack WithFixture(string embeddedResourceSuffix)
        => WithFixture(typeof(TestStack).Assembly, embeddedResourceSuffix);

    /// <summary>Loads a fixture from an embedded resource in <paramref name="assembly"/> by suffix.</summary>
    public TestStack WithFixture(Assembly assembly, string embeddedResourceSuffix)
    {
        _fixtureEnvelopes = FixtureReplayPublisher.LoadEnvelopes(assembly, embeddedResourceSuffix);
        return this;
    }

    /// <summary>Override the saga request timeout (default 10s in tests).</summary>
    public TestStack WithRequestTimeout(TimeSpan timeout)
    {
        _requestTimeout = timeout;
        return this;
    }

    /// <summary>Override the embedding stub latency profile.</summary>
    public TestStack WithEmbeddingLatency(LatencyProfile<string> latency)
    {
        _embeddingLatency = latency;
        return this;
    }

    /// <summary>Override the tagging stub latency profile.</summary>
    public TestStack WithTaggingLatency(LatencyProfile<string> latency)
    {
        _taggingLatency = latency;
        return this;
    }

    /// <summary>Override the embedding stub failure profile.</summary>
    public TestStack WithEmbeddingFailure(FailureProfile<string> failure)
    {
        _embeddingFailure = failure;
        return this;
    }

    /// <summary>Override the tagging stub failure profile.</summary>
    public TestStack WithTaggingFailure(FailureProfile<string> failure)
    {
        _taggingFailure = failure;
        return this;
    }

    /// <summary>Override the embedding stub output generator.</summary>
    public TestStack WithEmbeddingOutput(OutputGenerator<string, ReadOnlyMemory<float>> output)
    {
        _embeddingOutput = output;
        return this;
    }

    /// <summary>Override the tagging stub output generator.</summary>
    public TestStack WithTaggingOutput(OutputGenerator<string, TagResult> output)
    {
        _taggingOutput = output;
        return this;
    }

    // -------------------------------------------------------------------------
    // Observable accessor
    // -------------------------------------------------------------------------

    /// <summary>Observation sink for test assertions. Available after <see cref="StartAsync"/>.</summary>
    public ITestObservationSink Observations
    {
        get
        {
            EnsureStarted();
            return _provider!.GetRequiredService<ITestObservationSink>();
        }
    }

    /// <summary>
    /// Returns the underlying <see cref="ITestHarness"/> for tests that need MT-native
    /// consume/published/sent query surfaces (e.g. <c>harness.Consumed.Select&lt;T&gt;()</c>).
    /// Available after <see cref="StartAsync"/>.
    /// </summary>
    public ITestHarness GetTestHarness()
    {
        EnsureStarted();
        return _harness!;
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Boots containers (if required by tier), builds the DI container, starts the MT bus,
    /// and wires observers. Must be called before <see cref="PumpUntilQuiescent"/> or
    /// accessing <see cref="Observations"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await StartContainersAsync(ct);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _provider = services.BuildServiceProvider(validateScopes: true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();

        // Wire observers after bus start — must be connected before messages flow
        _observerHandles = ObservationWiring.ConnectTo(_harness.Bus, _provider);
        ObservationWiring.ConnectResponseObserver<AnalyzeMessageResponse>(_harness.Bus, _provider);
        ObservationWiring.ConnectResponseObserver<ProjectMessageResponse>(_harness.Bus, _provider);
        ObservationWiring.ConnectResponseObserver<TagMessageResponse>(_harness.Bus, _provider);
        ObservationWiring.ConnectResponseObserver<ClassifyMessageResponse>(_harness.Bus, _provider);
    }

    /// <summary>
    /// If a fixture was loaded via <see cref="WithFixture"/>, replays it in burst mode,
    /// then waits for the MT bus to drain or <paramref name="maxWait"/> elapses.
    /// If no fixture was loaded, waits passively until quiescent or timeout.
    /// </summary>
    public async Task PumpUntilQuiescent(TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        EnsureStarted();
        var timeout = maxWait ?? TimeSpan.FromSeconds(30);

        if (_fixtureEnvelopes is not null)
        {
            var publisher = _provider!.GetRequiredService<FixtureReplayPublisher>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await publisher.ReplayBurstAsync(_fixtureEnvelopes, cts.Token);
        }

        // Drain: wait until no messages are in-flight or timeout elapses
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(timeout);
        try
        {
            await _harness!.InactivityTask.WaitAsync(drainCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // maxWait elapsed — caller's assertions determine if this matters
        }
    }

    /// <summary>Stops the MT bus and tears down containers.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_observerHandles is not null)
            foreach (var h in _observerHandles) h.Dispose();

        if (_harness is not null)
            await _harness.Stop();

        if (_provider is not null)
            await _provider.DisposeAsync();

        foreach (var c in _containers)
            await c.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Internal: container startup
    // -------------------------------------------------------------------------

    private async Task StartContainersAsync(CancellationToken ct)
    {
        switch (_tier)
        {
            case TestStackTier.Unit:
                break; // no containers

            case TestStackTier.Integration:
            {
                var mongo = new MongoDbBuilder().Build();
                _containers.Add(mongo);
                await mongo.StartAsync(ct);
                _mongoConnectionString = mongo.GetConnectionString();
                break;
            }

            case TestStackTier.E2E:
            {
                var mongo = new MongoDbBuilder().Build();
                var rabbit = new RabbitMqBuilder().Build();
                var postgres = new PostgreSqlBuilder().Build();
                _containers.Add(mongo);
                _containers.Add(rabbit);
                _containers.Add(postgres);
                await Task.WhenAll(
                    mongo.StartAsync(ct),
                    rabbit.StartAsync(ct),
                    postgres.StartAsync(ct));
                _mongoConnectionString = mongo.GetConnectionString();
                _rabbitConnectionString = rabbit.GetConnectionString();
                // Postgres connection string stored for future use by callers
                // (pgvector setup beyond TestStack scope — see E2E XML comment)
                break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Internal: DI configuration per tier
    // -------------------------------------------------------------------------

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Clock
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(_ => DateTimeOffset.UtcNow);
        services.AddSingleton(clock);

        // Stubs
        var embedStub = new StubEmbeddingClient(
            _embeddingLatency ?? new LatencyProfile<string>.Constant(TimeSpan.Zero),
            _embeddingFailure ?? new FailureProfile<string>.None(),
            _embeddingOutput ?? OutputGeneratorHelpers.DeterministicEmbedding(768));

        var tagStub = new StubTaggingClient(
            _taggingLatency ?? new LatencyProfile<string>.Constant(TimeSpan.Zero),
            _taggingFailure ?? new FailureProfile<string>.None(),
            _taggingOutput ?? OutputGeneratorHelpers.DeterministicTags(3));

        services.AddSingleton<IEmbeddingClient>(embedStub);
        services.AddSingleton<ITaggingClient>(tagStub);
        services.AddSingleton<IVectorStore>(new InMemoryVectorStore());

        // Enrichment options — short timeouts for test speed
        services.AddSingleton(Options.Create(new EnrichmentTagOptions
        {
            RequestTimeout = _requestTimeout,
        }));
        services.AddSingleton(Options.Create(new EnrichmentClassifyOptions
        {
            RequestTimeout = _requestTimeout,
        }));

        // Stub repos used by ProjectMessageConsumer (channel/guild name lookups)
        var channelRepo = Substitute.For<IChannelNameRepo>();
        channelRepo
            .GetNamesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>()));
        services.AddSingleton(channelRepo);

        var roleRepo = Substitute.For<IGuildRoleNameRepo>();
        roleRepo
            .GetRoleNamesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>()));
        services.AddSingleton(roleRepo);

        // IMessageParser — registered via AddSingleton so MT consumer scope can resolve it
        services.AddSingleton<IMessageParser, MessageParser>();
        services.AddLogging(); // already added above; idempotent

        // Observations
        services.AddTestObservation()
                .ForResponseType<AnalyzeMessageResponse>()
                .ForResponseType<ProjectMessageResponse>()
                .ForResponseType<TagMessageResponse>()
                .ForResponseType<ClassifyMessageResponse>()
                .WithQueueDwellObserver();

        // Fixture publisher (always registered; replay only if fixture was loaded)
        services.AddSingleton<FixtureReplayPublisher>(sp =>
            new FixtureReplayPublisher(sp.GetRequiredService<IBus>()));

        switch (_tier)
        {
            case TestStackTier.Unit:
                ConfigureUnitBus(services);
                break;
            case TestStackTier.Integration:
                ConfigureIntegrationBus(services);
                break;
            case TestStackTier.E2E:
                ConfigureE2EBus(services);
                break;
        }
    }

    private void ConfigureUnitBus(IServiceCollection services)
    {
        services.AddMassTransitTestHarness(cfg =>
        {
            RegisterSagaInMemory(cfg);
            RegisterEnrichmentConsumers(cfg);
        });
    }

    private void ConfigureIntegrationBus(IServiceCollection services)
    {
        // BSON class maps must be registered before the Mongo saga repository starts
        // serializing/deserializing MessageSagaState — the IR sub-document uses a polymorphic
        // MessageNode hierarchy that requires explicit discriminator registration.
        MongoBsonRegistration.RegisterAll();

        var connStr = _mongoConnectionString!;
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connStr));
        services.AddSingleton<IMongoDatabase>(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase("discord_integration_test"));

        services.AddMassTransitTestHarness(cfg =>
        {
            RegisterSagaWithMongo(cfg);
            RegisterEnrichmentConsumers(cfg);
        });
    }

    private void ConfigureE2EBus(IServiceCollection services)
    {
        MongoBsonRegistration.RegisterAll();

        var connStr = _mongoConnectionString!;
        var rabbitConnStr = _rabbitConnectionString!;

        services.AddSingleton<IMongoClient>(_ => new MongoClient(connStr));
        services.AddSingleton<IMongoDatabase>(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase("discord_e2e_test"));

        // E2E uses RabbitMQ transport. AddMassTransitTestHarness wraps it and still provides
        // ITestHarness for InactivityTask / Consumed / Published queries.
        services.AddMassTransitTestHarness(cfg =>
        {
            cfg.UsingRabbitMq((ctx, rmq) =>
            {
                rmq.Host(new Uri(rabbitConnStr));
                rmq.ConfigureEndpoints(ctx);
            });

            RegisterSagaWithMongo(cfg);
            RegisterEnrichmentConsumers(cfg);
        });
    }

    // -------------------------------------------------------------------------
    // Shared saga registration helpers
    // -------------------------------------------------------------------------

    private static void RegisterSagaInMemory(IBusRegistrationConfigurator cfg)
    {
        cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
           .InMemoryRepository();
    }

    private static void RegisterSagaWithMongo(IBusRegistrationConfigurator cfg)
    {
        cfg.AddSagaStateMachine<MessageSagaStateMachine, MessageSagaState>()
           .MongoDbRepository(r =>
           {
               r.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
               r.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
               r.CollectionName = "message_sagas_test";
           });
    }

    private static void RegisterEnrichmentConsumers(IBusRegistrationConfigurator cfg)
    {
        // Enrichment consumers — resolve stubs from DI for IEmbeddingClient / ITaggingClient
        cfg.AddConsumer<Enrichment.Consumers.AnalyzeMessageConsumer,
                        Enrichment.Consumers.AnalyzeMessageConsumerDefinition>();
        cfg.AddConsumer<Enrichment.Consumers.TagConsumer,
                        Enrichment.Consumers.TagConsumerDefinition>();
        cfg.AddConsumer<Enrichment.Consumers.ClassifyConsumer,
                        Enrichment.Consumers.ClassifyConsumerDefinition>();

        // ProjectMessageConsumer — from Write assembly; uses IMessageParser + name repos from DI
        cfg.AddConsumer<Write.Consumers.ProjectMessageConsumer,
                        Write.Consumers.ProjectMessageConsumerDefinition>();
    }

    private void EnsureStarted()
    {
        if (_provider is null)
            throw new InvalidOperationException("Call StartAsync() before accessing TestStack members.");
    }
}
