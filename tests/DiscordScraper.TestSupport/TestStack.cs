using System.Reflection;
using DiscordScraper.Contracts.Clock;
using DiscordScraper.Contracts.Configuration;
using DiscordScraper.Contracts.Filters;
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

    // When true: E2E tier starts Mongo as a single-node replica set and registers the Mongo outbox
    // at the bus level — required to match the production MessageSagaDefinition which calls
    // UseMongoDbOutbox(context). Without this the outbox middleware call in the definition
    // throws because no IOutboxContextFactory is registered.
    private bool _useOutbox;

    // When true: replaces the real AnalyzeMessageConsumer with NeverRespondingAnalyzeConsumer
    // so every saga times out on the Analyze phase — the precondition for reproducing #11.
    private bool _useNeverRespondingAnalyze;

    // When set: injected into the logging pipeline so callers can inspect captured log entries.
    private CapturingLoggerProvider? _capturingLoggerProvider;

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

    /// <summary>
    /// E2E tier only. Starts Mongo as a single-node replica set and registers
    /// <c>AddMongoDbOutbox</c> at the bus level so that <see cref="MessageSagaDefinition"/>'s
    /// <c>UseMongoDbOutbox(context)</c> call resolves correctly. Required to reproduce #11
    /// because the outbox is active in production and changes the saga-consume pipeline ordering.
    /// </summary>
    public TestStack WithMongoOutbox()
    {
        _useOutbox = true;
        return this;
    }

    /// <summary>
    /// Injects a <see cref="CapturingLoggerProvider"/> into the logging pipeline.
    /// Access via <see cref="GetCapturingLogger"/> after <see cref="StartAsync"/> to inspect
    /// captured log entries, including Mongo E11000 duplicate-key errors from the saga repository.
    /// </summary>
    public TestStack WithCapturingLogger()
    {
        _capturingLoggerProvider = new CapturingLoggerProvider();
        return this;
    }

    /// <summary>
    /// Returns the capturing logger provider registered via <see cref="WithCapturingLogger"/>.
    /// Available after <see cref="StartAsync"/>.
    /// </summary>
    public CapturingLoggerProvider GetCapturingLogger()
    {
        if (_capturingLoggerProvider is null)
            throw new InvalidOperationException("Call WithCapturingLogger() before StartAsync().");
        return _capturingLoggerProvider;
    }

    /// <summary>
    /// Replaces <c>AnalyzeMessageConsumer</c> with <see cref="NeverRespondingAnalyzeConsumer"/>,
    /// which accepts every <c>AnalyzeMessageRequest</c> but never sends a response. Every saga
    /// remains in <c>AnalyzeMessage.Pending</c> until the configured request timeout fires.
    /// Use with a short <see cref="WithRequestTimeout"/> to drive a burst of
    /// <c>RequestTimeoutExpired&lt;AnalyzeMessageRequest&gt;</c> events.
    /// </summary>
    public TestStack WithNeverRespondingAnalyze()
    {
        _useNeverRespondingAnalyze = true;
        return this;
    }

    // -------------------------------------------------------------------------
    // Observable accessor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Observation sink for test assertions. Available after <see cref="StartAsync"/>.
    ///
    /// Gap measurement is driven by <see cref="OutboundTimestampFilter{T}"/> stamping a publish-time
    /// timestamp and <see cref="InboundTimestampFilter{T}"/> stamping a receive-time timestamp into
    /// each <see cref="IMeasured"/> message body. <see cref="ITestObservationSink.Consumes"/>
    /// records <c>PublishedAt</c> and <c>ReceivedOn</c> from the message body.
    /// No send-side observer is needed.
    ///
    /// <see cref="ITestObservationSink.QueueDwells"/> similarly uses the stamped body timestamp
    /// when available, falling back to broker <c>SentTime</c> for non-IStampable messages.
    /// </summary>
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

        // Wire observers after bus start — must be connected before messages flow.
        // QueueDwellObserver via ConnectTo; per-type ResponseConsumeObservers via ConnectResponseObserver.
        // OutboundTimestampFilter<T> stamps Timestamp on send/publish; InboundTimestampFilter<T>
        // stamps ReceivedOn on consume. Observers read both from PostConsume.
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
                // Outbox requires a replica set because Mongo transactions (used internally
                // by MassTransit's Mongo outbox for atomic saga + outbox writes) require
                // a replica set — standalone Mongo does not support multi-document transactions.
                var mongoBuilder = _useOutbox
                    ? new MongoDbBuilder().WithReplicaSet("rs0")
                    : new MongoDbBuilder();
                var mongo = mongoBuilder.Build();
                // masstransit/rabbitmq includes rabbitmq_delayed_message_exchange, which is required
                // for UseDelayedMessageScheduler() used by the saga request-timeout pipeline.
                // The default Testcontainers RabbitMQ image does not include this plugin.
                var rabbit = new RabbitMqBuilder().WithImage("masstransit/rabbitmq:latest").Build();
                var postgres = new PostgreSqlBuilder().Build();
                _containers.Add(mongo);
                _containers.Add(rabbit);
                _containers.Add(postgres);
                await Task.WhenAll(
                    mongo.StartAsync(ct),
                    rabbit.StartAsync(ct),
                    postgres.StartAsync(ct));
                // Replica set containers advertise their internal 127.0.0.1:27017 address
                // after initiation. The MongoDB client follows that topology advertisement and
                // tries to connect to the internal address, bypassing the mapped port.
                // directConnection=true forces the client to use the mapped endpoint directly.
                // replicaSet=rs0 is required alongside directConnection=true so the driver
                // treats the node as a replica set member, enabling session-based transactions
                // (which the Mongo outbox requires). Without replicaSet=, directConnection
                // implies standalone mode and StartSession/BeginTransaction fail.
                var rawConn = mongo.GetConnectionString();
                _mongoConnectionString = _useOutbox && !rawConn.Contains("directConnection")
                    ? rawConn.TrimEnd('/') + "?directConnection=true&replicaSet=rs0"
                    : rawConn;
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
        services.AddLogging(b =>
        {
            // Debug for outbox/saga activity when investigating E11000 storm — reduce to Warning after fix
            b.AddConsole().SetMinimumLevel(LogLevel.Debug)
              .AddFilter("MassTransit", LogLevel.Information)
              .AddFilter("Microsoft", LogLevel.Warning);
            if (_capturingLoggerProvider is not null)
                b.AddProvider(_capturingLoggerProvider);
        });

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

            // Explicit UsingInMemory to wire outbound+inbound timestamp filters on all pipes.
            // Without this, the default implicit InMemory factory runs without filter hooks.
            cfg.UsingInMemory((ctx, bus) =>
            {
                bus.UseSendFilter(typeof(OutboundTimestampFilter<>), ctx);
                bus.UsePublishFilter(typeof(OutboundTimestampFilter<>), ctx);
                bus.UseConsumeFilter(typeof(InboundTimestampFilter<>), ctx);
                bus.UseDelayedMessageScheduler();
                bus.ConfigureEndpoints(ctx);
            });
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

            cfg.UsingInMemory((ctx, bus) =>
            {
                bus.UseSendFilter(typeof(OutboundTimestampFilter<>), ctx);
                bus.UsePublishFilter(typeof(OutboundTimestampFilter<>), ctx);
                bus.UseConsumeFilter(typeof(InboundTimestampFilter<>), ctx);
                bus.UseDelayedMessageScheduler();
                bus.ConfigureEndpoints(ctx);
            });
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
            // Outbox bus-level registration must happen before endpoint configuration.
            // MessageSagaDefinition.ConfigureSaga calls UseMongoDbOutbox(context), which
            // requires IOutboxContextFactory to be resolvable at endpoint-configure time.
            // Without this registration, UseMongoDbOutbox throws at bus start.
            if (_useOutbox)
            {
                cfg.AddMongoDbOutbox(o =>
                {
                    o.QueryDelay = TimeSpan.FromMilliseconds(250);
                    o.ClientFactory(sp => sp.GetRequiredService<IMongoClient>());
                    o.DatabaseFactory(sp => sp.GetRequiredService<IMongoDatabase>());
                    o.UseBusOutbox();
                });
            }

            RegisterSagaWithMongo(cfg);
            RegisterEnrichmentConsumers(cfg, _useNeverRespondingAnalyze);

            cfg.UsingRabbitMq((ctx, rmq) =>
            {
                rmq.Host(new Uri(rabbitConnStr));
                rmq.UseSendFilter(typeof(OutboundTimestampFilter<>), ctx);
                rmq.UsePublishFilter(typeof(OutboundTimestampFilter<>), ctx);
                rmq.UseConsumeFilter(typeof(InboundTimestampFilter<>), ctx);
                rmq.UseDelayedMessageScheduler();
                rmq.ConfigureEndpoints(ctx);
            });
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

    private static void RegisterEnrichmentConsumers(
        IBusRegistrationConfigurator cfg,
        bool useNeverRespondingAnalyze = false)
    {
        // Analyze phase: real consumer or never-responding stub (for #11 timeout reproduction).
        if (useNeverRespondingAnalyze)
            cfg.AddConsumer<Stubs.NeverRespondingAnalyzeConsumer,
                            Stubs.NeverRespondingAnalyzeConsumerDefinition>();
        else
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
