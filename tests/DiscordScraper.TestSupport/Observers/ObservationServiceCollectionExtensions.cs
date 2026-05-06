using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// DI registration helpers for MT observability infrastructure in test projects.
///
/// Typical usage in a test harness:
/// <code>
/// services.AddTestObservation()
///         .ForResponseType&lt;AnalyzeMessageResponse&gt;()
///         .ForResponseType&lt;ProjectMessageResponse&gt;()
///         .ForResponseType&lt;TagMessageResponse&gt;()
///         .ForResponseType&lt;ClassifyMessageResponse&gt;();
/// </code>
///
/// Then retrieve <see cref="ITestObservationSink"/> from the container (or resolve
/// <see cref="TestObservationSink"/> directly) and wire the observers to the bus after
/// the harness starts:
/// <code>
/// var harness = provider.GetRequiredService&lt;ITestHarness&gt;();
/// await harness.Start();
/// var sink = provider.GetRequiredService&lt;ITestObservationSink&gt;();
/// ObservationWiring.ConnectTo(harness.Bus, provider, sink);
/// </code>
/// </summary>
public static class ObservationServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="TestObservationSink"/> as both
    /// <see cref="ITestObservationSink"/> and <see cref="TestObservationSink"/> (for Reset()),
    /// and registers the <see cref="RequestSendObserver"/>.
    ///
    /// Call <see cref="ForResponseType{TResponse}"/> on the returned builder to add per-type
    /// consume observers.
    /// </summary>
    public static ObservationBuilder AddTestObservation(this IServiceCollection services)
    {
        var sink = new TestObservationSink();
        services.AddSingleton<ITestObservationSink>(sink);
        services.AddSingleton(sink);
        services.AddSingleton<RequestSendObserver>(sp =>
            new RequestSendObserver(sp.GetRequiredService<ITestObservationSink>()));
        return new ObservationBuilder(services);
    }
}

/// <summary>
/// Fluent builder returned by <see cref="ObservationServiceCollectionExtensions.AddTestObservation"/>.
/// Chains <see cref="ForResponseType{TResponse}"/> calls to register per-type consume observers.
/// </summary>
public sealed class ObservationBuilder(IServiceCollection services)
{
    public IServiceCollection Services => services;

    /// <summary>
    /// Registers a <see cref="ResponseConsumeObserver{TResponse}"/> for the given response type.
    /// </summary>
    public ObservationBuilder ForResponseType<TResponse>() where TResponse : class
    {
        services.AddSingleton<ResponseConsumeObserver<TResponse>>(sp =>
            new ResponseConsumeObserver<TResponse>(sp.GetRequiredService<ITestObservationSink>()));
        return this;
    }

    /// <summary>
    /// Registers a <see cref="QueueDwellObserver"/> for read-side queue-dwell measurement.
    /// </summary>
    public ObservationBuilder WithQueueDwellObserver()
    {
        services.AddSingleton<QueueDwellObserver>(sp =>
            new QueueDwellObserver(sp.GetRequiredService<ITestObservationSink>()));
        return this;
    }
}

/// <summary>
/// Connects registered observers to a running bus.
/// Call after the test harness has started — observers must be connected before messages flow.
/// </summary>
public static class ObservationWiring
{
    /// <summary>
    /// Connects all registered observers to <paramref name="bus"/>. This must be called
    /// after <c>harness.Start()</c> and before the test sends any messages.
    ///
    /// Only connects observer types that were actually registered in <paramref name="provider"/>.
    /// </summary>
    public static ConnectHandle[] ConnectTo(IBus bus, IServiceProvider provider)
    {
        var handles = new List<ConnectHandle>();

        var sendObserver = provider.GetService<RequestSendObserver>();
        if (sendObserver is not null)
            handles.Add(bus.ConnectSendObserver(sendObserver));

        var dwellObserver = provider.GetService<QueueDwellObserver>();
        if (dwellObserver is not null)
            handles.Add(bus.ConnectConsumeObserver(dwellObserver));

        // Consume-message observers for each registered response type are connected via the
        // IConsumeMessageObserver<T> method. Callers that used ForResponseType<T> can retrieve
        // and connect these manually, or use the typed extension below.
        return handles.ToArray();
    }

    /// <summary>
    /// Connects a specific <see cref="ResponseConsumeObserver{TResponse}"/> to the bus.
    /// Returns null if the observer was not registered.
    /// </summary>
    public static ConnectHandle? ConnectResponseObserver<TResponse>(IBus bus, IServiceProvider provider)
        where TResponse : class
    {
        var observer = provider.GetService<ResponseConsumeObserver<TResponse>>();
        return observer is not null
            ? bus.ConnectConsumeMessageObserver(observer)
            : null;
    }
}
